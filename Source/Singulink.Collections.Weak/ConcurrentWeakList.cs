using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Singulink.Collections;

#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
#pragma warning disable SA1401 // Fields should be private
#pragma warning disable SA1649 // File name should match first type name
#pragma warning disable RCS1032 // Remove redundant parentheses
#pragma warning disable RCS1043 // Remove 'partial' modifier from type with a single part

// Helper methods for this type that don't need to be generic (save on generic instantiations):
using static Singulink.Collections.Helpers;
file static class Helpers
{
#if NET
    [StackTraceHidden]
#endif
    [DoesNotReturn]
    public static void ThrowDisposed(string objectName = nameof(ConcurrentWeakList<>))
    {
        throw new ObjectDisposedException(objectName);
    }

#if NET
    [StackTraceHidden]
#endif
    [DoesNotReturn]
    public static void ThrowInvalidIndex(string parameterName)
    {
        throw new ArgumentOutOfRangeException(parameterName, "Index is out of range.");
    }

#if NET
    [StackTraceHidden]
#endif
    [DoesNotReturn]
    public static void ThrowNodeWrongList()
    {
        throw new InvalidOperationException("The specified node does not belong to this list.");
    }

#if NET
    [StackTraceHidden]
#endif
    [DoesNotReturn]
    public static void ThrowInvalidOperationExceptionForEnumeration()
    {
        throw new InvalidOperationException(
            "Enumeration has not started or has already finished; please call MoveNext or MovePrevious to begin a new enumeration.");
    }

#if NET
    [StackTraceHidden]
#endif
    [DoesNotReturn]
    public static void ThrowArgumentExceptionForValueNotFound(string paramName)
    {
        throw new ArgumentException(
            "The specified value was not found in the list.", paramName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T? TryGetValue<T>(WeakReference<T>? wr) where T : class
    {
        if (wr is null) return null;
        if (!wr.TryGetTarget(out var result)) return null;
        return result;
    }
}

/// <summary>
/// Represents a collection of weakly referenced values that maintains relative insertion order. This type is also automatically safe for concurrent access at
/// an operation level (that is, individual operations are thread-safe, but your own custom compound operations may still need external synchronization).
/// </summary>
/// <remarks>
/// <para>Unlike <see cref="WeakList{T}" /> and <see cref="WeakCollection{T}" />, this type automatically cleans references on its own without requiring any
/// user interaction.</para>
/// <para>Note: this type scales better than <see cref="WeakList{T}" /> and <see cref="WeakCollection{T}" />, but has a higher overhead in scenarios where a
/// small number of items are stored.</para>
/// <para>Note: Highly contested scenarios may experience significant practical performance degradation due to lock contention, be aware of this when using in
/// such environments.</para>
/// <para>Note: all provided big O runtimes are strict, but those above O(log n) assume the case where no new nodes were added by another thread during the
/// operation - if new nodes were added concurrently, it may cause the operation to take longer than expected (e.g., if a concurrent operation happens to
/// always add a new node just after an enumeration's current node, then it will have to loop through all of those until it gets past them).</para>
/// <para>For optimal performance, avoid letting the finalizer run; instead, dispose the list explicitly or clear it - otherwise, the finalizer thread may be
/// blocked for a significant amount of time if the list is large.</para>
/// </remarks>
public sealed partial class ConcurrentWeakList<T> : IEnumerable<T>, IDisposable where T : class
{
#if NETSTANDARD
    // No DependentHandle type on .NET Standard, so we store the values in a CWT instead:
#if NETSTANDARD2_1_OR_GREATER
    private ConditionalWeakTable<T, LinkedList<InternalNode>>? _cwt = [];
#else
    // .NET Standard 2.0 doesn't have CWT.Clear, so we try to keep track of the keys so that we can clean it up ourselves:
    private ConditionalWeakTable<T, CwtEntry>? _cwt = new();
    private WeakReference<ConditionalWeakTable<T, CwtEntry>>? _cwtWeakRef;
    private LinkedList<WeakReference<CwtEntry>>? _cwtKeys = new();
    private ConditionalWeakTable<T, CwtEntry>.CreateValueCallback _createValueCallback;
    internal sealed class CwtEntry(ConditionalWeakTable<T, CwtEntry> cwt, object locker)
    {
        public WeakReference<T> Key;
        public LinkedListNode<WeakReference<CwtEntry>>? KeyNode;
        public LinkedList<InternalNode>? Entries = [];
        public WeakReference<ConditionalWeakTable<T, CwtEntry>> Cwt = new(cwt);
        public object Locker = locker;

        ~CwtEntry()
        {
            lock (Locker)
            {
                if (Cwt.TryGetTarget(out var cwt) && Key.TryGetTarget(out var key))
                {
                    if (cwt.TryGetValue(key, out var otherEntry))
                    {
                        if (otherEntry == this)
                        {
                            cwt.Remove(key);
                        }
                    }
                }
            }
        }
    }
#endif
#endif

    // The root node of the red-black tree:
    // Note: when not disposed, we always have at least one node (the pseudo-node), which is always ordered first.
    // When disposed, this is set to null.
    private Node? _root;

    // The current size of the list - note: we store as nint to make the size update operations faster (no overflow check needed).
    // It can be safely read with or without the lock held, but updates must be done with the lock held, and holding the lock is necessary to get an up-to-date
    // value.
    private nint _size;

    // Helper to assert not disposed in Debug mode (doesn't check in Release mode, but still gives nullable analysis info):
    [MemberNotNull(nameof(_root))]
#if NETSTANDARD
    [MemberNotNull(nameof(_cwt))]
#if !NETSTANDARD2_1_OR_GREATER
    [MemberNotNull(nameof(_cwtWeakRef))]
    [MemberNotNull(nameof(_cwtKeys))]
#endif
#endif
    partial void DebugAssertNotDisposed();
#if DEBUG
    partial void DebugAssertNotDisposed()
    {
        Debug.Assert(_root is not null, "Object is disposed.");
#if NETSTANDARD
        Debug.Assert(_cwt is not null, "_cwt should not be null since not disposed.");
#if !NETSTANDARD2_1_OR_GREATER
        Debug.Assert(_cwtWeakRef is not null, "_cwtWeakRef should not be null since not disposed.");
        Debug.Assert(_cwtKeys is not null, "_cwtKeys should not be null since not disposed.");
#endif
#endif
    }
#endif

#if NET9_0_OR_GREATER
    private readonly Lock _locker = new();
#else
    private readonly object _locker = new();
#endif

    // This allows us to track whether an item was added before or after an enumeration:
    private ulong _version;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcurrentWeakList{T}"/> class with no elements.
    /// </summary>
    public ConcurrentWeakList()
    {
        _root = new(this)
        {
            _color = Node.Color.Black,
            _subtreeSize = 1,
            _isPseudoNode = true,
        };
#if NETSTANDARD && !NETSTANDARD2_1_OR_GREATER
        _createValueCallback = (x) =>
        {
            CwtEntry entry = new(_cwt, _locker);
            entry.Key = new WeakReference<T>(x);
            entry.KeyNode = _cwtKeys.AddLast(new WeakReference<CwtEntry>(entry));
            return entry;
        };
        _cwtWeakRef = new(_cwt);
#endif
    }

    internal sealed class InternalNode
    {
        // Note: we cannot store the list as a strong reference in this type with the current implementation - the Node stores such a reference.
        // Our reference to the list - note, it's important that this is a weak reference as we directly reference it from InternalNode, then we will leak the
        // whole list due to https://github.com/dotnet/runtime/issues/12255.
        // Note: we use WeakHandle here to avoid needing to allocate a separate WeakReference object - otherwise it'd be WeakReference<Node>?.
        public WeakHandle _impl;

#if NETSTANDARD
        // No DependentHandle type on this framework, so we just use a normal WeakReference in here & use a CWT as backing store, and keep track of the node
        // that keeps this instance alive so that we can remove it if we dispose.
        public LinkedListNode<InternalNode>? _cwtNode;
#if !NETSTANDARD2_1_OR_GREATER
        public WeakReference<CwtEntry>? _cwtEntry;

        // Note: we must store CWT as a weak reference to avoid accessing it after its finalizer.
        public WeakReference<ConditionalWeakTable<T, CwtEntry>>? _cwt;
#endif
#else
        // We use DependentHandle to keep this Node alive at least as long as the value.
        public DependentHandle _dependentHandle;
#endif

#if !NET
        // Store the value (note: we have to use a weak reference, as it could contain a ref back to the list or Node):
        // Note: we use WeakHandle here to avoid needing to allocate a separate WeakReference object - otherwise it'd be WeakReference<T>?.
        // Note: we don't need this value except on .NET Standard, since we have it in the DependentHandle.
        public WeakHandle _value;
#endif

        // Keeps track of how many times we've attempted to finalize:
        // Note: we use this to avoid trying to wait a long time in the finalizer for the lock if it has contention - it is not required for correctness.
        // Note: we set this to -1 when it's been removed.
        public int _finalizeAttemptCount;

        private bool RemoveFromList(Node impl, WeakHandle original, bool disposing)
        {
            // Try to enter the lock now:
            var list = impl._list;
            bool entered = true;
            using var scope = (_finalizeAttemptCount < 5 && !disposing)
                ? list.TryEnterLock(out bool wasDisposed, out entered)
                : list.EnterLock(out wasDisposed);
            if (!wasDisposed)
            {
                if (entered)
                {
                    _finalizeAttemptCount = -1;
                    Thread.MemoryBarrier(); // Mark as removed.
                    try
                    {
                        list.DeleteHelper(impl);
                    }
                    finally
                    {
                        // Ensure node lives to at least here:
                        GC.KeepAlive(impl);

                        // It is important that we try to dispose the value or dependent handle while we hold the lock, so do that here:
#if NET
                        _dependentHandle.Dispose();
#else
                        _value.Dispose();

                        // Ensure removed from CWT tracking stuff (the remove logic might have missed it, since we're not necessarily alive anymore, so it
                        // might not be able to look up these lists):
                        _cwtNode?.List?.Remove(_cwtNode);
#if !NETSTANDARD2_1_OR_GREATER
                        if (Helpers.TryGetValue(_cwtEntry) is { } cwtEntry && cwtEntry.Entries.Count == 0)
                        {
                            if (_cwt.TryGetTarget(out var cwt) && cwtEntry.Key.TryGetTarget(out var key)) cwt.Remove(key);
                            cwtEntry.KeyNode?.List?.Remove(cwtEntry.KeyNode);
                            GC.SuppressFinalize(cwtEntry);
                        }
#endif
#endif
                    }
                }
                else
                {
                    // Let's just try again later as it might not be contested then (up to 5 times):
                    _finalizeAttemptCount++;
                    _impl.Handle = original.Handle;
                    Thread.MemoryBarrier(); // Ensure the handle gets updated before we exit the lock.
                    return false;
                }
            }
            else
            {
                // Dispose these in here, as we don't pass out "wasDisposed" to callers, which means they can't check for that (which is fine to leave to
                // dispose there, but they can't really check for that):
#if NET
                _dependentHandle.Dispose();
#else
                _value.Dispose();
#endif
            }

            // Success:
            return true;
        }

        private bool Dispose(bool disposing, Node? implValueFromCaller)
        {
            bool releaseHandle = true;
            WeakHandle impl = default;
            bool hasImplValue = false;
            try
            {
                // Get the node to remove, or discover if we've already been disposed:
                impl = new WeakHandle(Interlocked.Exchange(ref _impl.Handle, IntPtr.Zero));
                if (impl.Handle == IntPtr.Zero) return true;
                if (implValueFromCaller == null)
                {
                    if (impl.TryGetTarget<Node>() is not { } implValue) return true;
                    implValueFromCaller = implValue;
                }

                // Remove the node from the list:
                hasImplValue = true;
                if (!RemoveFromList(implValueFromCaller, impl, disposing))
                {
                    releaseHandle = false;
                    return false;
                }

#if NETSTANDARD
                // Set fields to null:
                if (!disposing) return true;
                _cwtNode = null;
#if !NETSTANDARD2_1_OR_GREATER
                _cwtEntry = null;
                _cwt = null;
#endif
#endif
            }
            finally
            {
                if (releaseHandle)
                {
#if NET
                    Debug.Assert(!(hasImplValue && _dependentHandle.IsAllocated), "These values should already have been disposed in RemoveFromList.");
                    _dependentHandle.Dispose();
#else
                    Debug.Assert(!(hasImplValue && _value.Handle != IntPtr.Zero), "These values should already have been disposed in RemoveFromList.");
                    _value.Dispose();
#endif
                    impl.Dispose();
                }
            }

            return true;
        }

        public void Dispose(Node? implValueFromCaller)
        {
            if (Dispose(disposing: true, implValueFromCaller)) GC.SuppressFinalize(this);
        }

        ~InternalNode()
        {
            if (!Dispose(disposing: false, implValueFromCaller: null)) GC.ReRegisterForFinalize(this);
        }

        // Helper for all the places where we want to quickly dispose the internal node from the list (e.g., for disposing the list):
        public void EarlyDispose()
        {
            var impl = new WeakHandle(Interlocked.Exchange(ref _impl.Handle, IntPtr.Zero));
            if (impl.Handle != IntPtr.Zero)
            {
#if NET9_0_OR_GREATER
                Debug.Assert(_impl.TryGetTarget<Node>()?.List._locker.IsHeldByCurrentThread != false, "Lock should be held by current thread.");
#elif DEBUG
                object? lockValue = _impl.TryGetTarget<Node>()?.List._locker;
                Debug.Assert(lockValue is null || Monitor.IsEntered(lockValue), "Lock should be held by current thread.");
#endif
                _finalizeAttemptCount = -1;
#if NET
                _dependentHandle.Dispose();
#else
                _value.Dispose();
#endif
                impl.Dispose();
                Thread.MemoryBarrier(); // Ensure the removed mark (_finalizeAttemptCount) is visible before we exit the lock.
                GC.SuppressFinalize(this);
                GC.KeepAlive(this); // Ensure it's alive long enough that we don't run the finalizer.
            }
        }
    }

    /// <summary>
    /// Represents a node in a <see cref="ConcurrentWeakList{T}" />.
    /// </summary>
    /// <remarks>
    /// Holding a strong reference to a <see cref="Node" /> does not prevent the value it references from being garbage collected.
    /// </remarks>
    public sealed partial class Node : IDisposable
    {
        // Private constructor:
        internal Node(ConcurrentWeakList<T> list) => _list = list;

        // We need a weak reference here as the Node will be strongly held by the data structure until it's removed, but the InternalNode is the thing that is
        // meant to be automatically GC'd.
        // Note: we don't use WeakHandle here, as we want to ensure that we're trivially thread-safe when trying to "dispose" it & read it simultaneously.
        internal WeakReference<InternalNode>? _node;

        // Helper method for testing & for internal use:
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal InternalNode? GetInternalNode()
        {
            var node = _node;
            if (node is null) return null;
            return node.TryGetTarget(out var result) ? result : null;
        }

        // Since we store the list here directly, we need to hold a weak ref back to Node from InternalNode:
        internal readonly ConcurrentWeakList<T> _list;

        // Our red-black tree state:
        internal Node? _parent;
        internal Node? _left;
        internal Node? _right;
        internal Color _color;
        internal bool _isPseudoNode;
        internal nint _subtreeSize; // The size of this node's subtree, including itself.
        internal ulong _version;  // The version of the list when this node was added.
        internal enum Color : byte
        {
            Black,
            Red,

            // Used to mark nodes that have been removed but are still alive for enumerations.
            // When set, the _right node is set to the next node in the list, so that we can continue enumerating.
            // Also, _left is set to the node that was previously ordered before this one, so that we can go backwards.
            Removed,
        }

        /// <summary>
        /// Gets the list that this node belongs to, or used to belong to.
        /// </summary>
        public ConcurrentWeakList<T> List
        {
            get
            {
                GC.KeepAlive(_list);
                return _list;
            }
        }

        /// <summary>
        /// Gets the target value of this node if it is still available, otherwise <see langword="null" />.
        /// </summary>
        /// <remarks>
        /// This method can return <see langword="null" /> even if the node has not yet been removed from the list, so don't try to use this to optimize
        /// avoiding calling <see cref="Dispose" /> unnecessarily; it's better to just call it unconditionally.
        /// </remarks>
        public T? Value
        {
            get
            {
                if (GetInternalNode() is not { } node) return null;

                // Note: we must take the lock here, as otherwise we could be partway through disposing or updating the value:
                // Note: we technically still could be partway through disposing after taking the lock, but not in a problematic way.
                if (_list._root is null) return null;
                using var scope = _list.EnterLock(out bool wasDisposed);
                if (wasDisposed) return null;

                // Do the actual get:
#if NET
                var dependentHandle = node._dependentHandle;
                if (!dependentHandle.IsAllocated) return null;
                object? result = dependentHandle.Target;
                Debug.Assert(result is T or null, "Stored target should be of the correct type or null.");
                return Unsafe.As<T?>(result);
#else
                var result = node._value;
                return result.TryGetTarget<T>();
#endif
            }
        }

        /// <summary>
        /// Disposes the node, removing it from the <see cref="ConcurrentWeakList{T}" /> it belongs to.
        /// </summary>
        public void Dispose()
        {
            // This implementation here doesn't need to be thread-safe, as the actual Dispose itself is thread-safe and supports multiple calls.
            if (GetInternalNode() is { } node) node.Dispose(this);
            _node = null;
            GC.KeepAlive(_list);
        }

#if !NETSTANDARD
        /// <summary>
        /// Try to update the target of this node to a new value.
        /// </summary>
        /// <exception cref="ArgumentNullException">If the value is null.</exception>
        /// <remarks>
        /// This method is only supported on frameworks that have <see cref="DependentHandle" />.
        /// </remarks>
        public bool TryUpdateTarget(T newTarget)
        {
            // Note: nothing in theory prevents us from implementing this on .NET Standard, but it would be more complex (due to having to update the CWT), so
            // we just don't support it there for now.
            ArgumentNullException.ThrowIfNull(newTarget); // RuntimeNullables seems to miss this somehow, so just add manually for now.
            if (GetInternalNode() is not { } node) return false;
            using var scope = _list.EnterLock(out bool wasDisposed);
            if (wasDisposed) return false;

            // Update the DependentHandle:
            if (!node._dependentHandle.IsAllocated) return false;
            object? oldTarget = node._dependentHandle.Target;
            DependentHandle dh = new(newTarget, node);
            var oldDh = node._dependentHandle;
            node._dependentHandle = dh;
            oldDh.Dispose();

            // Keep alive the old target, new target, and the list until after we've updated the handle:
            GC.KeepAlive(oldTarget);
            GC.KeepAlive(newTarget);
            return true;
        }
#endif

        /// <summary>
        /// Gets the version of the list when this node was added.
        /// </summary>
        public ListVersion Version => new(_version);

        /// <summary>
        /// Gets a value indicating whether this node has been removed from the list.
        /// </summary>
        public bool IsRemoved
        {
            get
            {
                // Note: when the lock is held, it is enough to just check the color, but otherwise checking IsRemoved is more up-to-date.
                if (GetInternalNode() is not { } node) return true;
                Thread.MemoryBarrier(); // Ensure we get the latest value.
                bool result = Volatile.Read(ref node._finalizeAttemptCount) == -1;
                GC.KeepAlive(_list);
                return result;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsRightChild(Node node)
    {
        return node._parent is not null && node._parent._right == node;
    }

    // Helper to allocate a node - doesn't set it up in the red-black tree.
    // Note: callers must GC.KeepAlive the value until after it is fully linked in.
    // Note: callers must hold the lock for the list when calling this and have already checked for disposal.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node AllocNode(T value)
    {
        DebugAssertNotDisposed();
        Node node = new(this);
        InternalNode internalNode = new();
        node._node = new WeakReference<InternalNode>(internalNode);
        internalNode._impl = WeakHandle.Alloc(node);
#if NETSTANDARD
        internalNode._value = WeakHandle.Alloc(value);
#if NETSTANDARD2_1_OR_GREATER
        internalNode._cwtNode = _cwt.GetValue(value, static (_) => []).AddLast(internalNode);
#else
        var cwtEntry = _cwt.GetValue(value, _createValueCallback);
        internalNode._cwtEntry = new WeakReference<CwtEntry>(cwtEntry);
        internalNode._cwtNode = cwtEntry.Entries.AddLast(internalNode);
        internalNode._cwt = _cwtWeakRef;
#endif
#else
        internalNode._dependentHandle = new DependentHandle(value, internalNode);
#endif

        // Note: our memory barrier to ensure the write is visible before the lock exits is in the caller (ManualAdd).
        _version++;
        Debug.Assert(_version > 0, "Version overflowed.");
        node._version = _version;
        return node;
    }

    // Adds to binary search tree at the given index, ignoring red-black tree rules - inserts as a red node.
    // This has the same restrictions as AllocNode, since it is not set up in a valid way for red-black trees.
    // Assumes that the caller validated the index.
    // Note: index is the caller index (0-based for real items); internally we offset by 1 to account for the pseudo-node.
    private Node BSTAdd(T value, nint index)
    {
        // Assert not disposed:
        DebugAssertNotDisposed();

        // Offset by 1 to account for the pseudo-node:
        nint implIndex = index + 1;

        // Find the node to place it under:
        Node parent = _root;
        bool becomeLeftChild;
        if (index == _size)
        {
            // Optimize for appending to end (common case) - go to rightmost node and insert as its right child:
            while (parent is { _right: not null }) parent = parent._right;
            becomeLeftChild = false;
        }
        else
        {
            // Loop normally, until we find the right place, using subtree size to calculate the index of the existing nodes at each step:
            nint childrenOrderedBeforeParent = (parent._left?._subtreeSize ?? 0) + 1;
            becomeLeftChild = implIndex < childrenOrderedBeforeParent;
            Node nextParent;
            while ((nextParent = becomeLeftChild ? parent._left : parent._right) != null)
            {
                parent = nextParent;
                if (becomeLeftChild)
                {
                    // Subtract one for old parent and new parent's right subtree's size:
                    childrenOrderedBeforeParent -= 1 + (parent._right?._subtreeSize ?? 0);
                }
                else
                {
                    // Add new parent's left subtree size + 1 for the parent itself:
                    childrenOrderedBeforeParent += (parent._left?._subtreeSize ?? 0) + 1;
                }

                // Decide which way to go next:
                becomeLeftChild = implIndex < childrenOrderedBeforeParent;
            }
        }

        // Call into ManualAdd:
        return ManualAdd(value, parent, becomeLeftChild);
    }

    // Similar to BSTAdd, but adds at the position provided by the caller instead of searching for it.
    // This has the same restrictions as AllocNode.
    // Note: the caller must guarantee that their provided parent and becomeLeftChild are valid.
    private Node ManualAdd(T value, Node parent, bool becomeLeftChild)
    {
        // Increment size:
        // Note: our memory barrier to ensure the write is visible before the lock exits is later in the function.
        _size++;
        Debug.Assert(_size > 0 && _size < (nint)(~(nuint)0 / 2), "Size overflowed.");

        // Set up the new node:
        Debug.Assert((becomeLeftChild ? parent._left : parent._right) == null, "Slot already occupied.");
        var node = AllocNode(value);
        (becomeLeftChild ? ref parent._left : ref parent._right) = node;
        node._parent = parent;
        node._color = Node.Color.Red;

        // Update subtree sizes up the tree (custom step - takes O(log n) time):
        node._subtreeSize = 1;
        do
        {
            parent._subtreeSize++;
            parent = parent._parent;
        }
        while (parent is not null);

        // Insert a memory barrier, to ensure that the new size & version are visible by the time the lock exits, to threads that do not re-enter it;
        // otherwise, nothing stops the write from being re-ordered after the lock is released.
        Thread.MemoryBarrier();

        // Return the new node:
        return node;
    }

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    // Note: the tree might not be valid for red-black rules after this is called, even if it was before.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LeftRotate(Node n)
    {
        /*
        Change this:

          X
         / \
        A   Y
           / \
          B   C

        To this:

            Y
           / \
          X   C
         / \
        A   B

        */

        // Get all the nodes/subtrees that we need:
        var x = n;
        var y = x._right;
        Debug.Assert(y is { }, "Caller should ensure n._right is not null.");
        var b = y._left;

        // Update the parent to point to y instead of x:
        if (x._parent is null)
        {
            _root = y;
            y._parent = null;
        }
        else if (IsRightChild(x))
        {
            x._parent._right = y;
            y._parent = x._parent;
        }
        else
        {
            x._parent._left = y;
            y._parent = x._parent;
        }

        // Update all of the child pointers:
        x._parent = y;
        x._right = b;
        y._left = x;
        b?._parent = x;

        // Update x & y's subtree sizes:
        x._subtreeSize += (b?._subtreeSize ?? 0) - y._subtreeSize;
        y._subtreeSize += x._subtreeSize - (b?._subtreeSize ?? 0);
    }

    // Same restrictions as LeftRotate.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RightRotate(Node n)
    {
        /*
        Change this:

            X
           / \
          Y   C
         / \
        A   B

        To this:

          Y
         / \
        A   X
           / \
          B   C

        */

        // Get all the nodes/subtrees that we need:
        var x = n;
        var y = x._left;
        Debug.Assert(y is { }, "Caller should ensure n._left is not null.");
        var b = y._right;

        // Update the parent to point to y instead of x:
        if (x._parent is null)
        {
            _root = y;
            y._parent = null;
        }
        else if (IsRightChild(x))
        {
            x._parent._right = y;
            y._parent = x._parent;
        }
        else
        {
            x._parent._left = y;
            y._parent = x._parent;
        }

        // Update all of the child pointers:
        x._parent = y;
        x._left = b;
        y._right = x;
        b?._parent = x;

        // Update x & y's subtree sizes:
        x._subtreeSize += (b?._subtreeSize ?? 0) - y._subtreeSize;
        y._subtreeSize += x._subtreeSize - (b?._subtreeSize ?? 0);
    }

    // Same restrictions as AllocNode.
    private void FixInsert(Node n)
    {
        // Assert not disposed:
        DebugAssertNotDisposed();

        // Loop while the node's parent is not black & node is not the root:
        while (n is { _parent._color: Node.Color.Red })
        {
            // Get the uncle node:
            var parent = n._parent;
            var grandparent = parent._parent;
            Debug.Assert(grandparent is { }, "Grandparent should not be null if parent is red.");
            var uncle = IsRightChild(parent) ? grandparent._left : grandparent._right;

            // If uncle is red:
            if (uncle is { _color: Node.Color.Red })
            {
                // Recolor parent & uncle to black, grandparent to red, and continue up the tree from grandparent:
                parent._color = Node.Color.Black;
                uncle._color = Node.Color.Black;
                grandparent._color = Node.Color.Red;
                n = grandparent;
            }
            else
            {
                // Check if triangle (convert into line):
                bool nIsRightChild = IsRightChild(n);
                if (nIsRightChild != IsRightChild(parent))
                {
                    // Rotate parent in opposite direction to n:
                    if (nIsRightChild) LeftRotate(parent);
                    else RightRotate(parent);

                    // Update nodes for next step (the rotate only results in these changes):
                    (n, parent) = (parent, n);
                }

                // Handle line:

                // Rotate grandparent in opposite direction to node:
                if (IsRightChild(n)) LeftRotate(grandparent);
                else RightRotate(grandparent);

                // Recolor parent to black and grandparent to red, then we're done:
                parent._color = Node.Color.Black;
                grandparent._color = Node.Color.Red;
                return;
            }
        }

        // Ensure root node is black:
        _root._color = Node.Color.Black;
    }

    // Handle failure in a way that doesn't potentially cause further corruption or finalizers to throw, etc.
    // Also implements the dispose logic.
    // The caller must hold the lock for the list when calling this.
    private void HandleFailureOrDispose()
    {
        // Assert not disposed yet:
        DebugAssertNotDisposed();

        // Mark disposed:
        // Note: we want other threads to be able to see it as soon as (in program order) we release the lock, even if they don't take it; hence the memory
        // barrier.
        var oldRoot = _root;
        _root = null;
        Thread.MemoryBarrier();

        // Exit the lock held by this thread now so that other threads can proceed:
#if NET9_0_OR_GREATER
        while (_locker.IsHeldByCurrentThread) _locker.Exit();
#else
        while (Monitor.IsEntered(_locker)) Monitor.Exit(_locker);
#endif

        // Clean out resources:
#if NETSTANDARD
#if NETSTANDARD2_1_OR_GREATER
        _cwt.Clear();
#else
        var cwtKeysCopy = _cwtKeys;
        _cwtKeys = null;
        foreach (var keyRef in cwtKeysCopy)
        {
            if (keyRef.TryGetTarget(out var key) && key.Key.TryGetTarget(out var target)) _cwt.Remove(target);
        }
#endif
        GC.KeepAlive(_cwt); // Ensure the CWT lives to here at least.
        _cwt = null;
#endif

        // Forget all nodes:
        ForgetNodeTree(oldRoot);
        void ForgetNodeTree(Node n)
        {
            if (n._left is not null) ForgetNodeTree(n._left);
            if (n._right is not null) ForgetNodeTree(n._right);
            n._color = Node.Color.Removed;
            n._left = null;
            n._right = null;
            n._parent = null;

            // Finalizer is not critical here, other than our handles & marking removed, so clean those up and then suppress:
            if (n.GetInternalNode() is { } node) node.EarlyDispose();

            // Set node to null:
            n._node = null;
        }

#if NET
        // Suppress finalizer for this list now, as we've cleaned up everything:
        GC.SuppressFinalize(this);
        GC.KeepAlive(this); // Ensure that if we call this method, nothing tries to run the finalizer after we've cleaned up
#endif
    }

#if NET
    /// <summary>
    /// Finalizes an instance of the <see cref="ConcurrentWeakList{T}"/> class.
    /// </summary>
    ~ConcurrentWeakList()
    {
        // Take the lock:
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) return;
        DebugAssertNotDisposed();
        HandleNode(_root);
        _root = null;

        // We loop through the whole list at once & free the DependentHandles rather than doing it part-by-part - this can block the finalizer thread for a
        // significant amount of time if the list is large.
        // If this causes issues for the caller, they should have properly disposed the list instead of letting the finalizer run.
        void HandleNode(Node n)
        {
            // Check if is removed node:
            if (n._color == Node.Color.Removed) return;

            // Handle internal node disposal:
            if (n.GetInternalNode() is { } node) node.EarlyDispose();

            // Recurse:
            if (n._left is not null) HandleNode(n._left);
            if (n._right is not null) HandleNode(n._right);

            // Clear references:
            n._color = Node.Color.Removed;
            n._left = null;
            n._right = null;
            n._parent = null;
            n._node = null;
        }
    }
#endif

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    // Note: the caller must guarantee that value is not null.
    private Node InsertAtHelper(T value, nint index)
    {
        try
        {
            var node = BSTAdd(value, index);
            FixInsert(node);
            Check();
            return node;
        }
        catch
        {
            HandleFailureOrDispose();
            throw;
        }
        finally
        {
            GC.KeepAlive(value);
        }
    }

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    // Note: the caller must guarantee that the provided parent and becomeLeftChild are valid.
    // Note: the caller must guarantee that value is not null.
    private Node InsertManualHelper(T value, Node parent, bool becomeLeftChild)
    {
        try
        {
            var node = ManualAdd(value, parent, becomeLeftChild);
            FixInsert(node);
            Check();
            return node;
        }
        catch
        {
            HandleFailureOrDispose();
            throw;
        }
        finally
        {
            GC.KeepAlive(value);
        }
    }

    // Helper method to check the integrity of the tree - only used in debug builds.
    partial void Check();
#if DEBUG
    partial void Check()
    {
        if (_root == null) return; // Disposed.

        // Check size makes sense:
        Debug.Assert(_size >= 0, "Size is negative.");

        // Check root is black:
        Debug.Assert(_root._color == Node.Color.Black, "Root node is not black.");

        // Check black descendent counts match & measures size matches claimed size:
        nint count = 0;
        Node? pseudoNode = null;
        BlackDescendentsCount(_root, ref count, ref pseudoNode, 0);
        Debug.Assert(count == _size, "Subtree size does not match actual size.");
        Debug.Assert(pseudoNode is not null, "Pseudo-node not found.");
        Debug.Assert(pseudoNode == GetNodeAtImpl(-1), "Node at index -1 is not the pseudo-node.");
    }

    private static int BlackDescendentsCount(Node n, ref nint count, ref Node? pseudoNode, int depth)
    {
        var l = n._left;
        var r = n._right;

        // Check depth makes sense:
        Debug.Assert(depth < 512, "Depth is way larger than should be possible (probably recursive definition).");

        // If red, both children must be black:
        if (n._color == Node.Color.Red)
        {
            Debug.Assert((l is null) || (l._color == Node.Color.Black), "Red node has red left child.");
            Debug.Assert((r is null) || (r._color == Node.Color.Black), "Red node has red right child.");
        }

        // Check that color is valid:
        Debug.Assert(n._color == Node.Color.Black || n._color == Node.Color.Red, "Node has invalid color.");

        // Check amount of black nodes in paths to descendents match:
        int lCount = l is not null ? BlackDescendentsCount(l, ref count, ref pseudoNode, depth + 1) : 0;
        int rCount = r is not null ? BlackDescendentsCount(r, ref count, ref pseudoNode, depth + 1) : 0;
        Debug.Assert(lCount == rCount, "Black descendent counts do not match.");

        // Check the left & right children's parent pointers:
        if (l is not null) Debug.Assert(l._parent == n, "Left child's parent pointer is incorrect.");
        if (r is not null) Debug.Assert(r._parent == n, "Right child's parent pointer is incorrect.");

        // If pseudo-node, ensure we only see one:
        if (n._isPseudoNode)
        {
            Debug.Assert(pseudoNode is null, "Multiple pseudo-nodes found.");
            pseudoNode = n;
        }
        else
        {
            count++;
        }

        Debug.Assert(n._subtreeSize > 0, "Node has subtree size of 0.");

        // Check subtree size:
        nint expectedSize = 1 + (l?._subtreeSize ?? 0) + (r?._subtreeSize ?? 0);
        Debug.Assert(n._subtreeSize == expectedSize, "Node subtree size is incorrect.");

        // Return black descendent count for this node, including self if black:
        return lCount + (n._color == Node.Color.Black ? 1 : 0);
    }
#endif

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    // Assumes that the caller validated the index.
    // Note: index is the caller index (0-based for real items); internally we offset by 1 to account for the pseudo-node.
    // Use index -1 to get the pseudo-node (internal index 0).
    private Node GetNodeAtImpl(nint index)
    {
        // Assert not disposed yet & check index:
        DebugAssertNotDisposed();
        Debug.Assert(index >= -1 && index < _size, "Index out of range.");

        // Offset by 1 to account for the pseudo-node:
        nint implIndex = index + 1;

        Node parent = _root;
        if (implIndex == parent._subtreeSize - 1)
        {
            // Optimize for getting last node - go to rightmost node and return it:
            while (parent is { _right: not null }) parent = parent._right;
            return parent;
        }
        else
        {
            // Loop normally, until we find the right place, using subtree size to calculate the index of the existing nodes at each step:
            nint childrenOrderedBeforeParent = (parent._left?._subtreeSize ?? 0) + 1;
            bool becomeLeftChild = implIndex < childrenOrderedBeforeParent;
            Node nextParent;
            while ((nextParent = becomeLeftChild ? parent._left : parent._right) != null)
            {
                // Check if we found the node:
                if (implIndex == childrenOrderedBeforeParent - 1) return parent;

                // Move down the tree:
                parent = nextParent;
                if (becomeLeftChild)
                {
                    // Subtract one for old parent and new parent's right subtree's size:
                    childrenOrderedBeforeParent -= 1 + (parent._right?._subtreeSize ?? 0);
                }
                else
                {
                    // Add new parent's left subtree size + 1 for the parent itself:
                    childrenOrderedBeforeParent += (parent._left?._subtreeSize ?? 0) + 1;
                }

                // Decide which way to go next:
                becomeLeftChild = implIndex < childrenOrderedBeforeParent;
            }

            // We should have found it by now:
            Debug.Assert(implIndex == childrenOrderedBeforeParent - 1, "Failed to find node at index.");
            return parent;
        }
    }

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node? GetNextNode(Node? n, out bool isRemovedNode)
    {
        // If n is null, return the first node if we have one:
        isRemovedNode = false;
        if (n is null && _size > 0) return GetNodeAtImpl(0);
        else if (n is null) return null;

        // Check if node has been removed as the caller needs to handle it specially:
        if (n is { _color: Node.Color.Removed })
        {
            isRemovedNode = true;
            return n;
        }

        // If we have a right child, go down that:
        if (n._right is not null)
        {
            n = n._right;
            while (n._left is not null) n = n._left;
            return n;
        }

        // Otherwise, go up until we find a parent that we are a left child of:
        while (n is not null && (n._parent is null || IsRightChild(n)))
        {
            n = n._parent;
        }

        // Return the parent, or null if we reached the root:
        return n?._parent;
    }

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Node? GetPrevNode(Node? n, out bool isRemovedNode)
    {
        // If n is null, return the last node if we have one:
        isRemovedNode = false;
        if (n is null && _size > 0) return GetNodeAtImpl(_size - 1);
        if (n is null) return null;

        // Check if node has been removed as the caller needs to handle it specially:
        if (n is { _color: Node.Color.Removed })
        {
            isRemovedNode = true;
            return n;
        }

        // If we have a left child, go down that:
        if (n._left is not null)
        {
            n = n._left;
            while (n._right is not null) n = n._right;
            if (n._isPseudoNode) return null;
            return n;
        }

        // Otherwise, go up until we find a parent that we are a right child of:
        while (n is not null && !IsRightChild(n))
        {
            n = n._parent;
        }

        // Return the parent, or null if we reached the root or the pseudo-node:
        Node? parent = n?._parent;
        if (parent?._isPseudoNode != false) return null;
        return parent;
    }

    // Prepares for BST deletion by preparing to remove using standard BST deletion logic and handling trivial recoloring cases.
    // Returns true if there's an extra black to fixup, false if not.
    private bool PrepareBSTDelete(ref Node n, out Node? prev, out Node? next)
    {
        // Get the nearby nodes:
        bool isRemovedNode;
        next = GetNextNode(n, out isRemovedNode);
        Debug.Assert(!isRemovedNode, "Node should be in tree.");
        prev = GetPrevNode(n, out isRemovedNode);
        Debug.Assert(!isRemovedNode, "Node should be in tree.");

        // Check if we have two children:
        if (n._left is not null && n._right is not null)
        {
            // Swap with the successor node:
            var successor = next;
            Debug.Assert(successor is not null, "Node with two children has no successor.");
            bool isNRightChild = IsRightChild(n);
            bool isSuccessorRightChild = IsRightChild(successor);
            bool successorIsDirectChild = successor._parent == n;
            n._subtreeSize = (successor._left?._subtreeSize ?? 0) + (successor._right?._subtreeSize ?? 0) + 1;
            successor._subtreeSize = n._left._subtreeSize + n._right._subtreeSize + 1;
            successor._left?._parent = n;
            successor._right?._parent = n;
            n._left._parent = successor;
            n._right._parent = successor;
            (n._parent, successor._parent) = (successor._parent, n._parent);
            (n._left, successor._left) = (successor._left, n._left);
            (n._right, successor._right) = (successor._right, n._right);
            (n._color, successor._color) = (successor._color, n._color);
            if (successorIsDirectChild) (n._parent, successor._right) = (successor, n);
            else if (n._parent is not null) (isSuccessorRightChild ? ref n._parent._right : ref n._parent._left) = n;
            else _root = n;
            if (successor._parent is not null) (isNRightChild ? ref successor._parent._right : ref successor._parent._left) = successor;
            else _root = successor;
        }

        // Handle 1 child case, and determine if we have an extra black to fixup:
        bool extraBlack = n._color == Node.Color.Black;
        Node? child = n._left ?? n._right;
        Debug.Assert(n._left is null || n._right is null, "Node has two children after swapping with successor.");
        if (child is not null)
        {
            // Rotate the node such that the child goes into its spot & it's a child of its current child:
            var parent = n._parent;
            if (parent == null) _root = child;
            else if (IsRightChild(n)) parent._right = child;
            else parent._left = child;
            child._parent = parent;
            FinishDestroyNode(n, prev, next);
            n = child;
            if (child is { _color: Node.Color.Red }) extraBlack = false;
        }

        // Ensure node is colored to black, as nil nodes are black:
        n._color = Node.Color.Black;

        // Return whether we have an extra black to fixup:
        return extraBlack;
    }

    // Finishes the BST deletion, which should be called after fixing up red-black properties:
    private void FinishBSTDelete(Node n, Node? prev, Node? next)
    {
        // Check we're not deleting the pseudo-node:
        Debug.Assert(!n._isPseudoNode, "Attempted to delete pseudo-node.");

        // Get the child (might be null):
        var parent = n._parent;
        Node? child = n._left ?? n._right;

        // Update parent to point to child & the child to point to parent:
        child?._parent = parent;
        if (parent is null) _root = child;
        else if (IsRightChild(n)) parent._right = child;
        else parent._left = child;

        // Destroy the node:
        FinishDestroyNode(n, prev, next);
    }

    // Finishes destroying a node by updating sizes, marking as removed, and cleaning up the internal node.
    private void FinishDestroyNode(Node n, Node? prev, Node? next)
    {
        // Update size:
        // Note: we need to use a memory barrier here, to ensure that the new size is visible by the time the lock exits, to threads that do not re-enter it;
        // otherwise, nothing stops the write from being re-ordered after the lock is released.
        _size--;
        Thread.MemoryBarrier();

        // Update subtree sizes up the tree (custom step - takes O(log n) time):
        var parent = n._parent;
        while (parent is not null)
        {
            parent._subtreeSize--;
            parent = parent._parent;
        }

        // Mark node as removed for enumerators & clear references:
        n._color = Node.Color.Removed;
        n._left = prev;
        n._right = next;
        n._parent = null;

        // Note - we don't have to clean up the internal node, as it's only possible to get to this method from the method in that class that already.
        Debug.Assert((n.GetInternalNode()?._impl.Handle).GetValueOrDefault() == IntPtr.Zero, "Should only be called through InternalNode's deletion.");

        // Free node's reference to internal node:
        n._node = null;
    }

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    // Fixes up red-black tree properties just before a deletion that left an extra black.
    private void FixDelete(Node n)
    {
        // Loop until either the root is double black (which we can just recolor to black), or until we recolor to single black:
        while (n is { _color: Node.Color.Black, _parent: not null })
        {
            // Get sibling node:
            var parent = n._parent;
            bool isRightChild = IsRightChild(n);
            var sibling = isRightChild ? parent._left : parent._right;
            Debug.Assert(sibling is { }, "Sibling should not be null during delete fixup.");

            // If sibling is red:
            if (sibling._color == Node.Color.Red)
            {
                // Recolor sibling to black, parent to red, rotate parent in direction of n, and continue:
                sibling._color = Node.Color.Black;
                parent._color = Node.Color.Red;
                if (isRightChild) RightRotate(parent);
                else LeftRotate(parent);
                isRightChild = IsRightChild(n);
                sibling = isRightChild ? parent._left : parent._right;
                Debug.Assert(sibling is { }, "Sibling should not be null during delete fixup.");
                Debug.Assert(parent == n._parent, "Parent changed unexpectedly during delete fixup.");
                if (sibling._color == Node.Color.Red) continue;
            }

            // Node's sibling is black.

            // If both of sibling's children are black:
            if (sibling._left is null or { _color: Node.Color.Black } && sibling._right is null or { _color: Node.Color.Black })
            {
                // Recolor sibling to red, move problem up the tree to parent:
                sibling._color = Node.Color.Red;
                n = parent;
            }
            else
            {
                // Get child of sibling that is the same direction as n and opposite:
                var sameDirNephew = isRightChild ? sibling._right : sibling._left;
                var oppositeDirNephew = isRightChild ? sibling._left : sibling._right;

                // If same dir nephew is red and opposite is black:
                if (sameDirNephew is { _color: Node.Color.Red } && oppositeDirNephew is null or { _color: Node.Color.Black })
                {
                    // Recolor same dir nephew to black, sibling to red, rotate sibling in opposite direction to n, update relevant nodes:
                    sameDirNephew._color = Node.Color.Black;
                    sibling._color = Node.Color.Red;
                    if (isRightChild) LeftRotate(sibling);
                    else RightRotate(sibling);
                    sibling = isRightChild ? parent._left : parent._right;
                    Debug.Assert(sibling is { }, "Sibling should not be null during delete fixup.");
                    oppositeDirNephew = isRightChild ? sibling._left : sibling._right;
                }

                // Now, opposite dir nephew must be red:
                Debug.Assert(oppositeDirNephew is { _color: Node.Color.Red }, "Opposite direction nephew is not red or is null during delete fixup.");

                // Recolor sibling to parent's color, parent to black, opposite dir nephew to black, rotate parent in direction of n:
                sibling._color = parent._color;
                parent._color = Node.Color.Black;
                oppositeDirNephew._color = Node.Color.Black;
                if (isRightChild) RightRotate(parent);
                else LeftRotate(parent);
                break;
            }
        }

        // Set color to single black:
        n._color = Node.Color.Black;
    }

    // The caller must hold the lock for the list when calling this and have already checked for disposal.
    // Note: the caller must guarantee that node is valid.
    private void DeleteHelper(Node node)
    {
        try
        {
            if (node._color == Node.Color.Removed) return; // Already removed.
            Node node2 = node;
            bool hasExtraBlack = PrepareBSTDelete(ref node2, out var prev, out var next);
            if (hasExtraBlack) FixDelete(node2);
            if (node == node2) FinishBSTDelete(node, prev, next);
            Check();
        }
        catch
        {
            HandleFailureOrDispose();
            throw;
        }
    }

#if NET9_0_OR_GREATER
    private ref struct LockScope(Lock locker, ConcurrentWeakList<T> list)
    {
        private Lock? _locker = locker;
        private ConcurrentWeakList<T>? _list = list;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (_locker is null) return;
            if (_locker.IsHeldByCurrentThread) _locker.Exit();
            _locker = null;

            // Keep list alive until after we exit the lock - this is important for many of the algorithms that use the lock:
            GC.KeepAlive(_list);
            _list = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LockScope EnterLock(out bool wasDisposed)
    {
        if (_root == null)
        {
            wasDisposed = true;
            return default;
        }

        SpinWait sw = default;
        while (true)
        {
            if (_locker.TryEnter())
            {
                wasDisposed = _root == null;
                if (wasDisposed) _locker.Exit();
                return wasDisposed ? default : new LockScope(_locker, this);
            }

            sw.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LockScope TryEnterLock(out bool wasDisposed, out bool entered)
    {
        if (_root == null)
        {
            wasDisposed = true;
            entered = false;
            return default;
        }

        if (_locker.TryEnter())
        {
            wasDisposed = _root == null;
            entered = !wasDisposed;
            if (wasDisposed) _locker.Exit();
            return wasDisposed ? default : new LockScope(_locker, this);
        }

        wasDisposed = false;
        entered = false;
        return default;
    }
#else
    private ref struct LockScope(object locker, ConcurrentWeakList<T> list)
    {
        private object? _locker = locker;
        private ConcurrentWeakList<T>? _list = list;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (_locker is null) return;
            if (Monitor.IsEntered(_locker)) Monitor.Exit(_locker);
            _locker = null;

            // Keep list alive until after we exit the lock - this is important for many of the algorithms that use the lock:
            GC.KeepAlive(_list);
            _list = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LockScope EnterLock(out bool wasDisposed)
    {
        if (_root == null)
        {
            wasDisposed = true;
            return default;
        }

        SpinWait sw = default;
        while (true)
        {
            if (Monitor.TryEnter(_locker))
            {
                wasDisposed = _root == null;
                if (wasDisposed) Monitor.Exit(_locker);
                return wasDisposed ? default : new LockScope(_locker, this);
            }

            sw.SpinOnce();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private LockScope TryEnterLock(out bool wasDisposed, out bool entered)
    {
        if (_root == null)
        {
            wasDisposed = true;
            entered = false;
            return default;
        }

        if (Monitor.TryEnter(_locker))
        {
            wasDisposed = _root == null;
            entered = !wasDisposed;
            if (wasDisposed) Monitor.Exit(_locker);
            return wasDisposed ? default : new LockScope(_locker, this);
        }

        wasDisposed = false;
        entered = false;
        return default;
    }
#endif

    /// <summary>
    /// Disposes the <see cref="ConcurrentWeakList{T}" />, removing all nodes and preventing further use.
    /// </summary>
    public void Dispose()
    {
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) return;
        HandleFailureOrDispose();
    }

    // The caller must hold the lock for the list when calling this.
    // Throws if the wrong list, and returns true if the node is still in the list, or false if not.
    private bool CheckNode(Node n)
    {
        if (n._list != this) ThrowNodeWrongList();
        return n._color != Node.Color.Removed;
    }

    /// <summary>
    /// Adds a value to the start of the list - takes O(log n) time.
    /// </summary>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    public Node AddFirst(T value)
    {
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) ThrowDisposed();
        return InsertAtHelper(value, 0);
    }

    /// <summary>
    /// Adds a value to the end of the list - takes O(log n) time.
    /// </summary>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    public Node AddLast(T value)
    {
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) ThrowDisposed();
        return InsertAtHelper(value, _size);
    }

    /// <summary>
    /// Adds a value at the specified index in the list - takes O(log n) time.
    /// </summary>
    /// <remarks>
    /// This method is considered unsafe, since nothing guarantees that the index didn't change between the time the index was obtained and the time this
    /// method is called.
    /// </remarks>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If the index is out of range.</exception>
    public Node UnsafeInsertAt(T value, nint index)
    {
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) ThrowDisposed();
        if (index < 0 || index > _size) ThrowInvalidIndex(nameof(index));
        return InsertAtHelper(value, index);
    }

    /// <summary>
    /// Gets the node at the specified index in the list - takes O(log n) time.
    /// </summary>
    /// <remarks>
    /// This method is considered unsafe, since nothing guarantees that the index didn't change between the time the index was obtained and the time this
    /// method is called.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If the index is out of range.</exception>
    public Node UnsafeGetNodeAt(nint index)
    {
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) ThrowDisposed();
        if (index < 0 || index >= _size) ThrowInvalidIndex(nameof(index));
        return GetNodeAtImpl(index);
    }

    // Helper for AddBefore and AddAfter
    private Node? AddNear(Node currentNode, T value, bool allowNearRemovedNode, bool addBefore)
    {
        // Validate parameters:
        CheckNode(currentNode);
        bool movedAlready = false;
        while (true)
        {
            // If we can tell we have a removed node without locking, handle now:
            if (currentNode is { _color: Node.Color.Removed })
            {
                if (!allowNearRemovedNode) return null;
                movedAlready = true;
                if (addBefore)
                {
                    do currentNode = currentNode._left;
                    while (currentNode is { _color: Node.Color.Removed });
                }
                else
                {
                    do currentNode = currentNode._right;
                    while (currentNode is { _color: Node.Color.Removed });
                }
            }

            // Enter lock & finish checking:
            using (EnterLock(out bool wasDisposed))
            {
                if (wasDisposed) ThrowDisposed();

                // Handle a removed node if needed by running the outer loop again:
                if (currentNode is { _color: Node.Color.Removed }) continue;

                // If we've already had to move due to a remove node, we want to add to the opposite side:
                if (movedAlready) addBefore = !addBefore;

                // Handle a node with a child on the side we want to add on:
                // Track original intent for null handling
                bool originalAddBefore = addBefore;
                if (addBefore)
                {
                    // Go to predecessor:
                    currentNode = GetPrevNode(currentNode, out bool isRemovedNode);
                    Debug.Assert(!isRemovedNode, "Node should be in tree.");
                    addBefore = false;
                }
                else
                {
                    // Go to successor:
                    currentNode = GetNextNode(currentNode, out bool isRemovedNode);
                    Debug.Assert(!isRemovedNode, "Node should be in tree.");
                    addBefore = true;
                }

                // If no node to add nearby:
                if (currentNode is null)
                {
                    // Add at start or end based on original intent:
                    // - Original addBefore=true means add before first → insert at 0
                    // - Original addBefore=false means add after last → insert at _size
                    return InsertAtHelper(value, originalAddBefore ? 0 : _size);
                }

                // If the child slot is already occupied, find the correct empty slot:
                // - If adding as left child but left is occupied, go to rightmost node in left subtree
                // - If adding as right child but right is occupied, go to leftmost node in right subtree
                if (addBefore && currentNode._left is not null)
                {
                    currentNode = currentNode._left;
                    while (currentNode._right is not null) currentNode = currentNode._right;
                    addBefore = false;
                }
                else if (!addBefore && currentNode._right is not null)
                {
                    currentNode = currentNode._right;
                    while (currentNode._left is not null) currentNode = currentNode._left;
                    addBefore = true;
                }

                // Add the node:
                return InsertManualHelper(value, currentNode, becomeLeftChild: addBefore);
            }
        }
    }

    /// <summary>
    /// Adds a value before specified node of the list - takes O(log n) time.
    /// </summary>
    /// <remarks>
    /// <para>The <see cref="AddBefore(Node, T)" /> override behaves as if <paramref name="allowBeforeRemovedNode"/> is <see langword="true" />.</para>
    /// <para>If a node has been removed, multiple adds near it might result in inconsistent ordering compared to if it was still in the list.</para>
    /// <para>If adding next to a removed node, then the O(log n) runtime is no longer guaranteed.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">If the node does not belong to this list.</exception>
    public Node? AddBefore(Node currentNode, T value, bool allowBeforeRemovedNode) => AddNear(currentNode, value, allowBeforeRemovedNode, addBefore: true);

    /// <inheritdoc cref="AddBefore(Node, T, bool)" />
    public Node AddBefore(Node currentNode, T value)
    {
        var result = AddBefore(currentNode, value, true);
        Debug.Assert(result is { }, "Result should not be null when allowing adding before removed node.");
        return result;
    }

    /// <summary>
    /// Adds a value after specified node of the list - takes O(log n) time.
    /// </summary>
    /// <remarks>
    /// <para>The <see cref="AddAfter(Node, T)" /> override behaves as if <paramref name="allowAfterRemovedNode"/> is <see langword="true" />.</para>
    /// <para>If a node has been removed, multiple adds near it might result in inconsistent ordering compared to if it was still in the list.</para>
    /// <para>If adding next to a removed node, then the O(log n) runtime is no longer guaranteed.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">If the node does not belong to this list.</exception>
    public Node? AddAfter(Node currentNode, T value, bool allowAfterRemovedNode) => AddNear(currentNode, value, allowAfterRemovedNode, addBefore: false);

    /// <inheritdoc cref="AddAfter(Node, T, bool)" />
    public Node AddAfter(Node currentNode, T value)
    {
        var result = AddAfter(currentNode, value, true);
        Debug.Assert(result is { }, "Result should not be null when allowing adding after removed node.");
        return result;
    }

    private Node? TryInsertNear(T existingValue, T value, IEqualityComparer<T>? comparer, bool addBefore)
    {
        comparer ??= EqualityComparer<T>.Default;

        var enumerator = GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerator.WasAddedDuringEnumeration) continue;
            var current = enumerator.CurrentNode;
            var currentValue = enumerator.Current;
            if (comparer.Equals(currentValue, existingValue))
            {
                using var scope = EnterLock(out bool wasDisposed);
                if (wasDisposed) ThrowDisposed();

                // Check if removed while we weren't holding the lock - we may as well make this somewhat atomic:
                if (current._color != Node.Color.Removed)
                {
                    var result = AddNear(current, value, allowNearRemovedNode: false, addBefore);
                    GC.KeepAlive(value);
                    GC.KeepAlive(currentValue);
                    Debug.Assert(result is not null, "Result should not be null when adding near non-removed node.");
                    return result;
                }
            }

            GC.KeepAlive(currentValue);
        }

        GC.KeepAlive(value);
        return null;
    }

    /// <summary>
    /// Tries to add a value before specified value of the list, or returns <see langword="null" /> - this method has the same runtime as
    /// <see cref="GetEnumerator()" /> plus O(n * comparer.Equals).
    /// </summary>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    public Node? TryInsertBefore(T existingValue, T value, IEqualityComparer<T>? comparer = null) => TryInsertNear(existingValue, value, comparer, addBefore: true);

    /// <summary>
    /// Tries to add a value before specified value of the list, or returns <see langword="null" /> - this method has the same runtime as
    /// <see cref="GetEnumerator()" /> plus O(n * comparer.Equals).
    /// </summary>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    public Node? TryInsertAfter(T existingValue, T value, IEqualityComparer<T>? comparer = null) => TryInsertNear(existingValue, value, comparer, addBefore: false);

    /// <summary>
    /// Tries to add a value before specified value of the list, or throws - this method has the same runtime as
    /// <see cref="GetEnumerator()" /> plus O(n * comparer.Equals).
    /// </summary>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    /// <exception cref="ArgumentException">If the specified existing value was not found.</exception>
    public Node InsertBefore(T existingValue, T value, IEqualityComparer<T>? comparer = null)
    {
        var result = TryInsertNear(existingValue, value, comparer, addBefore: true);
        if (result is null) ThrowArgumentExceptionForValueNotFound(nameof(existingValue));
        return result;
    }

    /// <summary>
    /// Tries to add a value before specified value of the list, or throws - this method has the same runtime as
    /// <see cref="GetEnumerator()" /> plus O(n * comparer.Equals).
    /// </summary>
    /// <exception cref="ArgumentNullException">If the value is null.</exception>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    /// <exception cref="ArgumentException">If the specified existing value was not found.</exception>
    public Node InsertAfter(T existingValue, T value, IEqualityComparer<T>? comparer = null)
    {
        var result = TryInsertNear(existingValue, value, comparer, addBefore: false);
        if (result is null) ThrowArgumentExceptionForValueNotFound(nameof(existingValue));
        return result;
    }

    /// <summary>
    /// Structure for enumerating over nodes in the list.
    /// </summary>
    public struct NodeEnumerator
    {
        internal readonly ConcurrentWeakList<T>? _list;
        internal Node? _currentNode;
        internal ulong _listVersion;

        internal NodeEnumerator(ConcurrentWeakList<T> list, Node? node)
        {
            _list = list;
            _currentNode = node;

            using var scope = _list.EnterLock(out bool wasDisposed);
            if (wasDisposed) _currentNode = null;

            _listVersion = list._version;
        }

        /// <summary>
        /// Helper API to support enumerating over an instance of <see cref="ConcurrentWeakList{T}.NodeEnumerator" />.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public readonly NodeEnumerator GetEnumerator() => this;

        [MemberNotNullWhen(false, nameof(_list))]
        internal readonly bool IsDisposed()
        {
            return _list is null or { _root: null };
        }

        /// <summary>
        /// Gets the current node in the enumeration.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the enumeration has not started or has already finished.</exception>
        public readonly Node Current
        {
            get
            {
                var currentNode = _currentNode;
                if (currentNode is null) ThrowInvalidOperationExceptionForEnumeration();
                GC.KeepAlive(_list);
                return currentNode;
            }
        }

        /// <summary>
        /// Moves to the next node in the enumeration.
        /// </summary>
        /// <remarks>
        /// <para>If the enumeration has not begun, or has reached the end, calling this method will move to the first valid node.</para>
        /// <para>If the enumerator or list has been disposed, this method will always return <see langword="false" />.</para>
        /// </remarks>
        public bool MoveNext()
        {
            // Check if we know it's disposed before locking:
            if (IsDisposed()) goto disposed;

            // Get the next node (unless it has been removed):
            Node? newNode = _currentNode;
            bool isRemovedNode;
            using (_list.EnterLock(out bool wasDisposed))
            {
                if (wasDisposed) goto disposed;
                newNode = _list.GetNextNode(newNode, out isRemovedNode);
            }

            // Handle removed node (it's important that we handle this outside the lock to control how long we hold it at most):
            // Since there's no way to guarantee that the non-removed node is still in the list by the time the caller uses it, we just try our best.
            if (isRemovedNode && newNode is not null)
            {
                do newNode = newNode._right;
                while (newNode is { _color: Node.Color.Removed } or { IsRemoved: true });
            }

            // Set the new node and return:
            _currentNode = newNode;
            return newNode is not null;

            // If disposed, clear current node and return false:
            disposed:
            _currentNode = null;
            return false;
        }

        /// <summary>
        /// Moves to the previous node in the enumeration.
        /// </summary>
        /// <remarks>
        /// <para>If the enumeration has not begun, or has reached the end, calling this method will move to the last valid node.</para>
        /// <para>If the enumerator or list has been disposed, this method will always return <see langword="false" />.</para>
        /// </remarks>
        public bool MovePrevious()
        {
            // Check if we know it's disposed before locking:
            if (IsDisposed()) goto disposed;

            // Get the previous node (unless it has been removed):
            Node? newNode = _currentNode;
            bool isRemovedNode = true;
            using (_list.EnterLock(out bool wasDisposed))
            {
                if (wasDisposed) goto disposed;
                newNode = _list.GetPrevNode(newNode, out isRemovedNode);
            }

            // Handle removed node (it's important that we handle this outside the lock to control how long we hold it at most):
            // Since there's no way to guarantee that the non-removed node is still in the list by the time the caller uses it, we just try our best.
            if (isRemovedNode && newNode is not null)
            {
                do newNode = newNode._left;
                while (newNode is { _color: Node.Color.Removed } or { IsRemoved: true });
            }

            // Set the new node and return:
            _currentNode = newNode;
            return newNode is not null;

            // If disposed, clear current node and return false:
            disposed:
            _currentNode = null;
            return false;
        }

        /// <summary>
        /// Gets a value indicating whether the current node was added to the list during the enumeration.
        /// </summary>
        /// <exception cref="NullReferenceException">May be thrown if the current node is not valid.</exception>
        public readonly bool WasAddedDuringEnumeration => _currentNode!._version > _listVersion;

        /// <summary>
        /// Gets an enumerable for the remaining nodes in the enumeration.
        /// </summary>
        /// <param name="reversed">If <see langword="true" />, the enumeration will be in reverse order.</param>
        /// <param name="skipNewNodes">If <see langword="true" />, nodes added during enumeration will be skipped.</param>
        /// <remarks>
        /// Nodes that have values which have been collected will not be skipped.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">If the enumerator has been disposed.</exception>
        public readonly IEnumerable<Node> AsEnumerable(bool reversed = false, bool skipNewNodes = false)
        {
            if (_list is null) ThrowDisposed("NodeEnumerator");
            GC.KeepAlive(_list);
            return new HeapNodeEnumerable(this, reversed, skipNewNodes);
        }
    }

    /// <summary>
    /// Structure for enumerating over values in the list.
    /// </summary>
    public struct Enumerator
    {
        internal NodeEnumerator _nodeEnumerator;
        private T? _value;

        internal Enumerator(NodeEnumerator nodeEnumerator)
        {
            _nodeEnumerator = nodeEnumerator;
            _value = null;
        }

        /// <summary>
        /// Helper API to support enumerating over an instance of <see cref="ConcurrentWeakList{T}.Enumerator" />.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public readonly Enumerator GetEnumerator() => this;

        /// <summary>
        /// Gets the current node in the enumeration.
        /// </summary>
        /// <exception cref="ObjectDisposedException">If the enumerator has been disposed.</exception>
        /// <exception cref="InvalidOperationException">If the enumeration has not started or has already finished.</exception>
        public readonly Node CurrentNode => _nodeEnumerator.Current;

        /// <summary>
        /// Moves to the next value in the enumeration.
        /// </summary>
        /// <remarks>
        /// <para>If the enumeration has not begun, or has reached the end, calling this method will move to the first valid node.</para>
        /// <para>If the enumerator or list has been disposed, this method will always return <see langword="false" />.</para>
        /// </remarks>
        public bool MoveNext()
        {
            do
            {
                if (!_nodeEnumerator.MoveNext())
                {
                    _value = null;
                    return false;
                }
            }
            while ((_value = _nodeEnumerator.Current.Value) is null);
            return true;
        }

        /// <summary>
        /// Moves to the previous value in the enumeration.
        /// </summary>
        /// <remarks>
        /// <para>If the enumeration has not begun, or has reached the end, calling this method will move to the last valid node.</para>
        /// <para>If the enumerator or list has been disposed, this method will always return <see langword="false" />.</para>
        /// </remarks>
        public bool MovePrevious()
        {
            do
            {
                if (!_nodeEnumerator.MovePrevious())
                {
                    _value = null;
                    return false;
                }
            }
            while ((_value = _nodeEnumerator.Current.Value) is null);
            return true;
        }

        /// <summary>
        /// Gets the current value in the enumeration.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This API may return <see langword="null" /> on first call, even if provided a valid starting node, until either <see cref="MoveNext" /> or
        /// <see cref="MovePrevious" /> is called.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">If the enumeration has not started or has already finished.</exception>
        public readonly T Current
        {
            get
            {
                var value = _value;
                if (value is null) ThrowInvalidOperationExceptionForEnumeration();
                return value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the current value was added to the list during the enumeration.
        /// </summary>
        /// <exception cref="NullReferenceException">May be thrown if the current value is not valid.</exception>
        public readonly bool WasAddedDuringEnumeration => _nodeEnumerator.WasAddedDuringEnumeration;

        /// <summary>
        /// Gets an enumerable for the remaining items in the enumeration.
        /// </summary>
        /// <param name="reversed">If <see langword="true" />, the enumeration will be in reverse order.</param>
        /// <param name="skipNewNodes">If <see langword="true" />, nodes added during enumeration will be skipped.</param>
        /// <remarks>
        /// Nodes that have values which have been collected will be skipped.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">If the enumerator has been disposed.</exception>
        public readonly IEnumerable<T> AsEnumerable(bool reversed = false, bool skipNewNodes = false)
        {
            if (_nodeEnumerator._list is null) ThrowDisposed("ValueEnumerator");
            GC.KeepAlive(_nodeEnumerator._list);
            return new HeapValueEnumerable(this, reversed, skipNewNodes);
        }
    }

    private sealed partial class HeapValueEnumerator(Enumerator impl, bool reversed, bool skipNewNodes) : IEnumerator<T>
    {
        private Enumerator _impl = impl;

        public T Current => _impl.Current;
        object IEnumerator.Current => _impl.Current;
        public void Dispose() => _impl = default;

        public bool MoveNext()
        {
            do
            {
                if (!(reversed ? _impl.MovePrevious() : _impl.MoveNext())) return false;
            }
            while (skipNewNodes && _impl.WasAddedDuringEnumeration);
            return true;
        }

        public void Reset() => throw new NotSupportedException();
    }

    private sealed partial class HeapValueEnumerable(Enumerator impl, bool reversed, bool skipNewNodes) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            var inst = impl;
            inst._nodeEnumerator._listVersion = impl._nodeEnumerator._list!.Version._version;
            return new HeapValueEnumerator(inst, reversed, skipNewNodes);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed partial class HeapNodeEnumerator(NodeEnumerator impl, bool reversed, bool skipNewNodes) : IEnumerator<Node>
    {
        private NodeEnumerator _impl = impl;

        public Node Current => _impl.Current;
        object IEnumerator.Current => _impl.Current;
        public void Dispose() => _impl = default;

        public bool MoveNext()
        {
            do
            {
                if (!(reversed ? _impl.MovePrevious() : _impl.MoveNext())) return false;
            }
            while (skipNewNodes && _impl.WasAddedDuringEnumeration);
            return true;
        }

        public void Reset() => throw new NotSupportedException();
    }

    private sealed partial class HeapNodeEnumerable(NodeEnumerator impl, bool reversed, bool skipNewNodes) : IEnumerable<Node>
    {
        public IEnumerator<Node> GetEnumerator()
        {
            var inst = impl;
            var list = impl._list;
            Debug.Assert(list is not null, "List should not be null, as we can only box enumerators while they're not disposed.");
            inst._listVersion = list.Version._version;
            return new HeapNodeEnumerator(inst, reversed, skipNewNodes);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Gets an enumerator for values in the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This function only enumerates the available values, and skips nodes whose values have been collected.
    /// </para>
    /// <para>
    /// New nodes added during enumeration may be included in the enumeration, depending on timing; see <see cref="Enumerator.WasAddedDuringEnumeration" />
    /// and <see cref="Enumerator.AsEnumerable(bool, bool)" /> for controlling this behavior.
    /// </para>
    /// <inheritdoc cref="GetNodeEnumerator()" path="/remarks/*[position()>2]" />
    /// </remarks>
    public Enumerator GetEnumerator() => new(GetNodeEnumerator());

    /// <summary>
    /// Gets an enumerator for the values in the list that enumerates from the <paramref name="startNode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="startNode"/> will not be included in the enumeration; enumeration will begin from the next valid node after or before it.
    /// </para>
    /// <inheritdoc cref="GetEnumerator()" path="/remarks/*" />
    /// </remarks>
    /// <exception cref="InvalidOperationException">If the specified node does not belong to this list.</exception>
    public Enumerator GetEnumerator(Node startNode) => new(GetNodeEnumerator(startNode));

    /// <summary>
    /// Gets an enumerator for nodes in the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This function enumerates over all nodes in the list, regardless of whether their values have been collected yet or not (value removal is not immediate).
    /// </para>
    /// <para>
    /// New nodes added during enumeration may be included in the enumeration, depending on timing; see <see cref="NodeEnumerator.WasAddedDuringEnumeration" />
    /// and <see cref="NodeEnumerator.AsEnumerable(bool, bool)" /> for controlling this behavior.
    /// </para>
    /// <para>
    /// Given a list with no new nodes being added concurrently, full enumeration will take O(n log n) in the worst case, or O(n) time to complete in the
    /// common case (no nodes removed concurrently).
    /// </para>
    /// <para>
    /// An individual enumeration step takes O(log n) time in the worst case, due to tree traversal.
    /// </para>
    /// <para>
    /// These big O runtimes assume no nodes being added concurrently, see <see cref="ConcurrentWeakList{T}" /> for remarks about that case.
    /// </para>
    /// </remarks>
    public NodeEnumerator GetNodeEnumerator() => new(this, null);

    /// <summary>
    /// Gets an enumerator for nodes in the list that enumerates from the <paramref name="startNode"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="startNode"/> will not be included in the enumeration; enumeration will begin from the next valid node after or before it.
    /// </para>
    /// <inheritdoc cref="GetNodeEnumerator()" path="/remarks/*" />
    /// </remarks>
    /// <exception cref="InvalidOperationException">If the specified node does not belong to this list.</exception>
    public NodeEnumerator GetNodeEnumerator(Node startNode)
    {
        CheckNode(startNode);
        return new(this, startNode);
    }

    /// <inheritdoc />
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new HeapValueEnumerator(GetEnumerator(), reversed: false, skipNewNodes: false);

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => new HeapValueEnumerator(GetEnumerator(), reversed: false, skipNewNodes: false);

    /// <summary>
    /// <para>
    /// Performs a locked operation on the list, allowing multiple operations to be completed without potential intermediate changes.
    /// </para>
    /// <para>
    /// Note: holding the lock for more than a short period of time may cause finalizer starvation due to blocking the finalizer thread, hence why this API is
    /// considered unsafe; if you need to perform long-running multi-part operations, you should use your own different lock and ensure you handle concurrent
    /// removal with it from the list's internal lock (this setup won't block the finalizer).
    /// </para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    public void UnsafePerformLockedOperation<TState>(TState state, Action<ConcurrentWeakList<T>, TState> operation)
#if NET9_0_OR_GREATER
        where TState : allows ref struct
#endif
    {
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) ThrowDisposed();
        operation(this, state);
    }

    /// <summary>
    /// <para>
    /// Attempts to perform a locked operation on the list, allowing multiple operations to be completed without potential intermediate changes.
    /// </para>
    /// <para>
    /// If the lock could not be immediately acquired, or the list has been disposed, the operation will not be performed and <see langword="false" /> will be
    /// returned.
    /// </para>
    /// <para>
    /// Note: holding the lock for more than a short period of time may cause finalizer starvation due to blocking the finalizer thread, hence why this API is
    /// considered unsafe; if you need to perform long-running multi-part operations, you should use your own different lock and ensure you handle concurrent
    /// removal with it from the list's internal lock (this setup won't block the finalizer).
    /// </para>
    /// </summary>
    public bool UnsafeTryPerformLockedOperation<TState>(TState state, Action<ConcurrentWeakList<T>, TState> operation)
#if NET9_0_OR_GREATER
        where TState : allows ref struct
#endif
    {
        using var scope = TryEnterLock(out bool wasDisposed, out bool entered);
        if (wasDisposed || !entered) return false;
        operation(this, state);
        return true;
    }

    /// <summary>
    /// Gets the number of nodes in the list.
    /// </summary>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    public nint Count
    {
        get
        {
            // Note: we need an memory barrier here, since otherwise the read might not be sequentially consistent (volatile alone is not enough).
            Thread.MemoryBarrier();
            nint size = Volatile.Read(ref _size);
            if (_root == null) ThrowDisposed();
            GC.KeepAlive(this);
            return size;
        }
    }

    /// <summary>
    /// Removes the specified node from the list if it is still in the list - takes O(log n) time.
    /// </summary>
    /// <exception cref="InvalidOperationException">If the specified node never belonged to this list.</exception>
    public void Remove(Node node)
    {
        CheckNode(node);
        node.Dispose();
        GC.KeepAlive(this);
    }

    /// <summary>
    /// Removes the first instance of the specified value from the list if it is in the list - this method has the same runtime as
    /// <see cref="GetEnumerator()" /> plus O(n * comparer.Equals).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method enumerates the list looking for a matching node; if nodes are added by other threads during the operation, they will not be included.
    /// </para>
    /// </remarks>
    public bool Remove(T value, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;

        var enumerator = GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerator.WasAddedDuringEnumeration) continue;
            var current = enumerator.CurrentNode;
            var currentValue = enumerator.Current;
            if (comparer.Equals(currentValue, value))
            {
                // Make the operation somewhat atomic (i.e., if someone else removed it while we weren't holding the lock, we consider that as happened before
                // us and thus not counting as a successful removal here):
                using var scope = EnterLock(out bool wasDisposed);
                if (wasDisposed)
                {
                    GC.KeepAlive(currentValue);
                    break;
                }
                else if (current._color == Node.Color.Removed)
                {
                    GC.KeepAlive(currentValue);
                    continue;
                }

                current.Dispose();
                GC.KeepAlive(currentValue);
                GC.KeepAlive(value);
                return true;
            }

            GC.KeepAlive(currentValue);
        }

        GC.KeepAlive(value);
        return false;
    }

    /// <summary>
    /// Removes all nodes from the list - takes O(n log n) time if no new nodes are added concurrently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method removes all nodes one-by-one; using <see cref="Dispose" /> is faster if you do not need to reuse the list instance.
    /// </para>
    /// <para>
    /// This big O runtime assume no nodes being added concurrently, see <see cref="ConcurrentWeakList{T}" /> for remarks about that case.
    /// </para>
    /// </remarks>
    public void Clear()
    {
        // We clear by repeatedly deleting any node we can find until none are left - it is important we do it like this to ensure we only block for O(log n)
        // time at most at once. If the user wants to clear "properly", they should call Dispose and create a new instance.

        var enumerator = GetNodeEnumerator();

        while (enumerator.MoveNext())
        {
            if (!enumerator.WasAddedDuringEnumeration) enumerator.Current.Dispose();
        }
    }

    /// <summary>
    /// Finds the first node matching the specified predicate - this method has the same runtime as <see cref="GetEnumerator()" /> plus O(n * predicate).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method enumerates the list looking for a matching node; if nodes are added by other threads during the operation, they will not be included.
    /// </para>
    /// </remarks>
    public (Node Node, T Value)? Find(Predicate<T> match)
    {
        var enumerator = GetEnumerator();

        while (enumerator.MoveNext())
        {
            if (enumerator.WasAddedDuringEnumeration) continue;
            var current = enumerator.CurrentNode;
            var currentValue = enumerator.Current;
            if (match(currentValue))
            {
                // Make the operation somewhat atomic (i.e., if someone else removed it while we weren't holding the lock, we consider that as happened before
                // us and thus not counting as a successful find here):
                using var scope = EnterLock(out bool wasDisposed);
                if (wasDisposed)
                {
                    GC.KeepAlive(currentValue);
                    break;
                }
                else if (current._color == Node.Color.Removed)
                {
                    GC.KeepAlive(currentValue);
                    continue;
                }

                GC.KeepAlive(currentValue);
                return (current, currentValue);
            }

            GC.KeepAlive(currentValue);
        }

        return null;
    }

    /// <summary>
    /// Finds the first node that is considered equal according to the comparer - this method has the same runtime as <see cref="GetEnumerator()" /> plus
    /// O(n * comparer.Equals).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method enumerates the list looking for a matching node; if nodes are added by other threads during the operation, they will not be included.
    /// </para>
    /// </remarks>
    public (Node Node, T Value)? Find(T value, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;

        var enumerator = GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerator.WasAddedDuringEnumeration) continue;
            var current = enumerator.CurrentNode;
            var currentValue = enumerator.Current;
            if (comparer.Equals(currentValue, value))
            {
                // Make the operation somewhat atomic (i.e., if someone else removed it while we weren't holding the lock, we consider that as happened before
                // us and thus not counting as a successful find here):
                using var scope = EnterLock(out bool wasDisposed);
                if (wasDisposed)
                {
                    GC.KeepAlive(value);
                    break;
                }
                else if (current._color == Node.Color.Removed)
                {
                    GC.KeepAlive(value);
                    continue;
                }

                GC.KeepAlive(currentValue);
                return (current, currentValue);
            }

            GC.KeepAlive(currentValue);
        }

        return null;
    }

    /// <summary>
    /// Determins if the list contains any node that is considered equal according to the comparer - this method has the same runtime as
    /// <see cref="GetEnumerator()" /> plus O(n * comparer.Equals).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method enumerates the list looking for a matching node; if nodes are added by other threads during the operation, they will not be included.
    /// </para>
    /// </remarks>
    public bool Contains(T value, IEqualityComparer<T>? comparer = null)
    {
        // Just delegate to Find:
        return Find(value, comparer).HasValue;
    }

    /// <summary>
    /// Gets the index of the specified node in the list, or -1 if the node has been removed - takes O(log n) time.
    /// </summary>
    /// <remarks>
    /// This method is considered unsafe, since nothing guarantees that the index didn't change between the time the index was obtained and the time this
    /// method is called.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">If the specified node has not or does not belong to this list.</exception>
    public nint UnsafeGetIndexOfNode(Node node)
    {
        using var scope = EnterLock(out bool wasDisposed);
        if (wasDisposed) ThrowDisposed();

        if (!CheckNode(node)) return -1;

        // Calculate 1-based rank (position in in-order traversal including pseudo-node):
        Node originalNode = node;
        nint rank = (node._left?._subtreeSize ?? 0) + 1;
        while (node._parent is not null)
        {
            // If we came from the right subtree, add parent's left subtree size + 1 (for the parent itself)
            if (IsRightChild(node)) rank += (node._parent._left?._subtreeSize ?? 0) + 1;
            node = node._parent;
        }

        // Convert 1-based rank to caller index (0-based, skipping pseudo-node which is at rank 1):
        // Note: the first -1 is to convert from 1-based to 0-based, the second -1 is to skip the pseudo-node.
        nint index = rank - 2;

        Debug.Assert(index >= 0 && index < _size, "Calculated index is out of range.");
        Debug.Assert(GetNodeAtImpl(index) == originalNode, "Calculated index does not point to the correct node.");

        return index;
    }

    /// <summary>
    /// Represents a version of the list at a point in time - the version is increased only whenever an item is added to the list.
    /// </summary>
    /// <remarks>
    /// This allows callers to detect whether a node was added after they begun a long operation they split up into multiple steps to not starve finalizers.
    /// </remarks>
    public readonly struct ListVersion : IEquatable<ListVersion>, IComparable<ListVersion>
    {
        internal readonly ulong _version;
        internal ListVersion(ulong version) => _version = version;

        /// <inheritdoc />
        public readonly override int GetHashCode() => _version.GetHashCode();

        /// <inheritdoc />
        public readonly override bool Equals(object? obj) => obj is ListVersion other && _version == other._version;

        /// <inheritdoc cref="IEquatable{T}.Equals(T)" />
        public readonly bool Equals(ListVersion other) => _version == other._version;

        /// <inheritdoc cref="IComparable{T}.CompareTo(T)" />
        public readonly int CompareTo(ListVersion other) => _version.CompareTo(other._version);

        /// <summary>
        /// Determines whether two versions are equal.
        /// </summary>
        public static bool operator ==(ListVersion left, ListVersion right) => left._version == right._version;

        /// <summary>
        /// Determines whether two versions are not equal.
        /// </summary>
        public static bool operator !=(ListVersion left, ListVersion right) => left._version != right._version;

        /// <summary>
        /// Determines whether the first version is greater than or equal to the second.
        /// </summary>
        public static bool operator >=(ListVersion left, ListVersion right) => left._version >= right._version;

        /// <summary>
        /// Determines whether the first version is less than or equal to the second.
        /// </summary>
        public static bool operator <=(ListVersion left, ListVersion right) => left._version <= right._version;

        /// <summary>
        /// Determines whether the first version is greater than the second.
        /// </summary>
        public static bool operator >(ListVersion left, ListVersion right) => left._version > right._version;

        /// <summary>
        /// Determines whether the first version is less than the second.
        /// </summary>
        public static bool operator <(ListVersion left, ListVersion right) => left._version < right._version;
    }

    /// <summary>
    /// Gets the version of the list - this allows callers to detect whether a node was added after they begun a non-atomic operation.
    /// </summary>
    /// <exception cref="ObjectDisposedException">If the instance has been disposed.</exception>
    public ListVersion Version
    {
        get
        {
            // Note: we need an memory barrier here, since otherwise the read might not be sequentially consistent (volatile alone is not enough).
            Thread.MemoryBarrier();
            ulong version = Volatile.Read(ref _version);
            if (_root == null) ThrowDisposed();
            GC.KeepAlive(this);
            return new ListVersion(version);
        }
    }
}
