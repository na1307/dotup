using Dotup.Json.Registry;
using Dotup.Services;
using Semver;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions.TestingHelpers;

namespace Dotup.Tests;

[Collection("Sequential")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names often use underscores")]
public sealed class RegistryServiceTests : IDisposable {
    private const string TestRoot = "/test/dotup";
    private readonly MockFileSystem fileSystem;
    private readonly RegistryService service;

    public RegistryServiceTests() {
        Environment.SetEnvironmentVariable("DOTUP_ROOT", TestRoot);
        fileSystem = new();
        fileSystem.AddDirectory(TestRoot);
        service = new(fileSystem);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("DOTUP_ROOT", null);

    [Fact]
    public async Task GetRegistryAsync_ShouldReturnRegistry_WhenFileExists() {
        // Arrange
        var registryPath = Path.Combine(TestRoot, "registry.json");

        const string json = """
                            {
                              "cli-version": "10.0.100",
                              "installed-channels": {
                                "10.0": {
                                  "sdk-version": "10.0.100",
                                  "runtime-version": "10.0.0",
                                  "sdk-manifests": []
                                }
                              }
                            }
                            """;

        fileSystem.AddFile(registryPath, new(json));

        // Act
        var result = await service.GetRegistryAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("10.0.100", result.CliVersion?.ToString());
        Assert.True(result.InstalledChannels.ContainsKey("10.0"));
    }

    [Fact]
    public async Task GetRegistryAsync_ShouldThrowFileNotFoundException_WhenFileDoesNotExist() {
        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.GetRegistryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrModifyRegistryAsync_ShouldAddNewChannel_WhenItDoesNotExist() {
        // Arrange
        DotupRegistry registry = new();
        var sdkVersion = SemVersion.Parse("10.0.100");
        var runtimeVersion = SemVersion.Parse("10.0.0");

        // Act
        await service.AddOrModifyRegistryAsync("10.0", registry, sdkVersion, runtimeVersion, [], true, TestContext.Current.CancellationToken);

        // Assert
        var registryPath = Path.Combine(TestRoot, "registry.json");
        Assert.True(fileSystem.FileExists(registryPath));

        var savedJson = await fileSystem.File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
        Assert.Contains("10.0", savedJson);
        Assert.Contains("10.0.100", savedJson);
    }

    [Fact]
    public async Task RemoveFromRegistryAsync_ShouldRemoveChannel() {
        // Arrange
        DotupRegistry registry = new();
        var sdkVersion = SemVersion.Parse("10.0.100");
        var runtimeVersion = SemVersion.Parse("10.0.0");

        await service.AddOrModifyRegistryAsync("10.0", registry, sdkVersion, runtimeVersion, [], true, TestContext.Current.CancellationToken);

        // Act
        await service.RemoveFromRegistryAsync("10.0", registry, TestContext.Current.CancellationToken);

        // Assert
        var registryPath = Path.Combine(TestRoot, "registry.json");
        var savedJson = await fileSystem.File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"10.0\":", savedJson); // Channel key should be gone
    }
}
