namespace ProjectMemory.Tests.Database;

using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProjectMemory.Configuration;
using ProjectMemory.Database;
using ProjectMemory.Routing;

/// <summary>
/// Tests for <see cref="PersistenceServiceCollectionExtensions"/>
/// </summary>
[TestClass]
public sealed class PersistenceServiceCollectionExtensionsTests
{
    #region Private Fields

    private string _testRoot = null!;

    #endregion

    #region Tests Constructors

    #endregion

    #region Test Preparation

    [TestInitialize]
    public void Initialize()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "ProjectMemory.Tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_testRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    #endregion

    #region Tests
    
    /// <summary>
    /// Verifies that all persistence services can be constructed from the dependency injection container.
    /// </summary>
    [TestMethod]
    public void RegisterServices()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(new ProjectMemoryOptions { DataDirectory = _testRoot }));
        services.AddPersistence();
        
        using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.IsNotNull(serviceProvider.GetRequiredService<IProjectRegistry>());
        Assert.IsNotNull(serviceProvider.GetRequiredService<RoutingManager>());
    }

    #endregion

    #region Private Methods

    #endregion

    #region Test Classes

    #endregion
}
