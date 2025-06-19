namespace CacheUtility.Tests
{
    public class GetAllByGroupTests : IDisposable
    {
        public GetAllByGroupTests()
        {
            // Clean up cache before each test
            Cache.RemoveAll();
        }

        public void Dispose()
        {
            // Clean up cache after each test
            Cache.RemoveAll();
            Cache.Dispose();
        }

        [Fact]
        public void GetAllByGroup_WithValidGroup_ReturnsAllItemsInGroup()
        {
            // Arrange
            const string groupName = "TestGroup";
            Cache.Get("key1", groupName, () => "value1");
            Cache.Get("key2", groupName, () => "value2");
            Cache.Get("key3", groupName, () => "value3");

            // Act
            var result = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.Equal(3, result.Count);
            Assert.Equal("value1", result["key1"]);
            Assert.Equal("value2", result["key2"]);
            Assert.Equal("value3", result["key3"]);
        }

        [Fact]
        public void GetAllByGroup_WithEmptyGroup_ReturnsEmptyDictionary()
        {
            // Arrange
            const string groupName = "EmptyGroup";

            // Act
            var result = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void GetAllByGroup_WithNullGroupName_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => Cache.GetAllByGroup(null));
        }

        [Fact]
        public void GetAllByGroup_WithMixedDataTypes_ReturnsAllItemsWithCorrectTypes()
        {
            // Arrange
            const string groupName = "MixedGroup";
            var testDate = DateTime.Now;
            var testList = new List<string> { "A", "B", "C" };

            Cache.Get("stringKey", groupName, () => "Hello World");
            Cache.Get("intKey", groupName, () => 42);
            Cache.Get("dateKey", groupName, () => testDate);
            Cache.Get("listKey", groupName, () => testList);

            // Act
            var result = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.Equal(4, result.Count);
            Assert.Equal("Hello World", result["stringKey"]);
            Assert.Equal(42, result["intKey"]);
            Assert.Equal(testDate, result["dateKey"]);
            Assert.Equal(testList, result["listKey"]);
        }

        [Fact]
        public void GetAllByGroup_AfterGroupRemoval_ReturnsEmptyDictionary()
        {
            // Arrange
            const string groupName = "RemovalTestGroup";
            Cache.Get("key1", groupName, () => "value1");
            Cache.Get("key2", groupName, () => "value2");

            // Verify items are cached
            var beforeRemoval = Cache.GetAllByGroup(groupName);
            Assert.Equal(2, beforeRemoval.Count);

            // Act
            Cache.RemoveGroup(groupName);
            var afterRemoval = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.NotNull(afterRemoval);
            Assert.Empty(afterRemoval);
        }
    }
}