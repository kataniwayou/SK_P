using System.Linq;
using System.Reflection;
using BaseApi.Core.Persistence.Repositories;
using Xunit;

namespace BaseApi.Tests.Persistence;

public sealed class RepositorySurfaceTests
{
    [Fact]
    public void Test_IRepository_ExposesExactlyFiveMethods()
    {
        var methods = typeof(IRepository<>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToList();

        Assert.Equal(5, methods.Count);

        var names = methods.Select(m => m.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "AddAsync", "DeleteAsync", "GetAsync", "ListAsync", "Update" }, names);

        foreach (var m in methods)
        {
            Assert.False(
                m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(IQueryable<>),
                $"{m.Name} must not return IQueryable<> (D-04 surface lock)");
        }
    }
}
