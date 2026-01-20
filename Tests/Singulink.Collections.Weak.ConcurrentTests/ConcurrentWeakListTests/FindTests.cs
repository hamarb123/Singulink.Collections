namespace Singulink.Collections.Weak.ConcurrentTests.ConcurrentWeakListTests;

[PrefixTestClass]
public class FindTests
{
    [TestMethod]
    public void FindInEmptyList()
    {
        var list = new ConcurrentWeakList<object>();

        var result = list.Find((_) => true);

        result.ShouldBeNull();
    }

    [TestMethod]
    public void FindFirstItem()
    {
        var list = new ConcurrentWeakList<object>();
        object value1 = new();
        object value2 = new();
        object value3 = new();
        var node1 = list.AddLast(value1);
        list.AddLast(value2);
        list.AddLast(value3);

        var result = list.Find((x) => x == value1);

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(node1);
        result.Value.Value.ShouldBeSameAs(value1);

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindMiddleItem()
    {
        var list = new ConcurrentWeakList<object>();
        object value1 = new();
        object value2 = new();
        object value3 = new();
        list.AddLast(value1);
        var node2 = list.AddLast(value2);
        list.AddLast(value3);

        var result = list.Find((x) => x == value2);

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(node2);
        result.Value.Value.ShouldBeSameAs(value2);

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindLastItem()
    {
        var list = new ConcurrentWeakList<object>();
        object value1 = new();
        object value2 = new();
        object value3 = new();
        list.AddLast(value1);
        list.AddLast(value2);
        var node3 = list.AddLast(value3);

        var result = list.Find((x) => x == value3);

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(node3);
        result.Value.Value.ShouldBeSameAs(value3);

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindNonexistentItem()
    {
        var list = new ConcurrentWeakList<object>();
        object value1 = new();
        object value2 = new();
        object searchValue = new();
        list.AddLast(value1);
        list.AddLast(value2);

        var result = list.Find((x) => x == searchValue);

        result.ShouldBeNull();

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(searchValue);
    }

    [TestMethod]
    public void FindWithCustomPredicate()
    {
        var list = new ConcurrentWeakList<string>();
        string value1 = "apple";
        string value2 = "banana";
        string value3 = "cherry";
        list.AddLast(value1);
        var node2 = list.AddLast(value2);
        list.AddLast(value3);

        var result = list.Find((x) => x.StartsWith("b"));

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(node2);
        result.Value.Value.ShouldBe("banana");

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindValueWithDefaultComparer()
    {
        var list = new ConcurrentWeakList<string>();
        string value1 = "apple";
        string value2 = "banana";
        string value3 = "cherry";
        list.AddLast(value1);
        var node2 = list.AddLast(value2);
        list.AddLast(value3);

        var result = list.Find("banana");

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(node2);
        result.Value.Value.ShouldBe("banana");

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindValueWithCustomComparer()
    {
        var list = new ConcurrentWeakList<string>();
        string value1 = "apple";
        string value2 = "BANANA";
        string value3 = "cherry";
        list.AddLast(value1);
        var node2 = list.AddLast(value2);
        list.AddLast(value3);

        var result = list.Find("banana", StringComparer.OrdinalIgnoreCase);

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(node2);
        result.Value.Value.ShouldBe("BANANA");

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindDoesNotIncludeItemAddedDuringSearch()
    {
        var list = new ConcurrentWeakList<object>();
        object value1 = new();
        object value2 = new();
        list.AddLast(value1);
        list.AddLast(value2);

        object addedDuringSearch = new();
        bool addedItem = false;

        // Use a predicate that adds an item mid-search
        var result = list.Find((x) =>
        {
            if (!addedItem)
            {
                list.AddLast(addedDuringSearch);
                addedItem = true;
            }

            return x == addedDuringSearch;
        });

        // Should not find the item added during search
        result.ShouldBeNull();

        // But the item should be in the list now
        list.Count.ShouldBe(3);

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(addedDuringSearch);
    }

    [TestMethod]
    public void FindSkipsNodeRemovedDuringSearch()
    {
        var list = new ConcurrentWeakList<object>();
        object value1 = new();
        object value2 = new();
        object value3 = new();
        list.AddLast(value1);
        var node2 = list.AddLast(value2);
        var node3 = list.AddLast(value3);

        bool removedNode = false;

        // Remove node2 when we see value1, then look for value2
        var result = list.Find((x) =>
        {
            if (!removedNode)
            {
                list.Remove(node2);
                removedNode = true;
            }

            return x == value2;
        });

        // Should not find value2 since it was removed
        result.ShouldBeNull();

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindSkipsNodeRemovedAfterCurrentPosition()
    {
        var list = new ConcurrentWeakList<object>();
        object value1 = new();
        object value2 = new();
        object value3 = new();
        list.AddLast(value1);
        var node2 = list.AddLast(value2);
        list.AddLast(value3);

        int callCount = 0;

        // When at value1, remove value2 (which is after current position)
        var result = list.Find((x) =>
        {
            callCount++;
            if (x == value1)
            {
                list.Remove(node2);
            }

            return x == value2;
        });

        // Should not find value2 since it was removed after current position
        result.ShouldBeNull();

        // We should have visited value1 and value3, but not value2
        callCount.ShouldBe(2);

        GC.KeepAlive(value1);
        GC.KeepAlive(value2);
        GC.KeepAlive(value3);
    }

    [TestMethod]
    public void FindInLargeList()
    {
        var list = new ConcurrentWeakList<object>();
        var values = Enumerable.Range(0, 1000).Select((_) => new object()).ToList();
        var nodes = values.Select(list.AddLast).ToList();

        // Find item near the end
        object targetValue = values[950];
        var targetNode = nodes[950];

        var result = list.Find((x) => x == targetValue);

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(targetNode);
        result.Value.Value.ShouldBeSameAs(targetValue);

        GC.KeepAlive(values);
    }

    [TestMethod]
    public void FindFirstMatchInListWithDuplicates()
    {
        var list = new ConcurrentWeakList<string>();
        string value = "duplicate";
        var node1 = list.AddLast(value);
        list.AddLast(value);
        list.AddLast(value);

        var result = list.Find(value);

        result.ShouldNotBeNull();
        result.Value.Node.ShouldBeSameAs(node1);
        result.Value.Value.ShouldBe(value);

        GC.KeepAlive(value);
    }

    [TestMethod]
    public void FindWithNullPredicateThrows()
    {
        var list = new ConcurrentWeakList<object>();

        Should.Throw<ArgumentNullException>(() => list.Find((Func<object, bool>)null!));
    }
}
