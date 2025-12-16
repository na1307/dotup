using Dotup.Json.Registry;
using Dotup.Json.ReleasesIndex;
using Dotup.Services;
using Moq;
using Moq.Protected;
using Semver;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Abstractions.TestingHelpers;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Dotup.Tests;

[Collection("Sequential")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names often use underscores")]
public sealed class SdkInstallationServiceTests : IDisposable {
    private const string TestRoot = "/test/dotup";
    private readonly MockFileSystem fileSystem;
    private readonly Mock<IRegistryService> registryServiceMock;
    private readonly Mock<ITarExtractor> tarExtractorMock;
    private readonly SdkInstallationService service;

    public SdkInstallationServiceTests() {
        Environment.SetEnvironmentVariable("DOTUP_ROOT", TestRoot);
        fileSystem = new();
        fileSystem.AddDirectory(TestRoot);
        registryServiceMock = new();
        tarExtractorMock = new();
        service = new(fileSystem, registryServiceMock.Object, tarExtractorMock.Object);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("DOTUP_ROOT", null);

    [Fact]
    public async Task InstallSdkAsync_ShouldDownloadAndExtractSdk() {
        // Arrange
        var sdkVersion = SemVersion.Parse("10.0.100");
        var runtimeVersion = SemVersion.Parse("10.0.0");
        Uri sdkUrl = new("https://example.com/sdk.tar.gz");

        // Create a fake tar.gz content
        byte[] fileContent;

        await using (MemoryStream ms = new()) {
            await using (GZipStream gzip = new(ms, CompressionMode.Compress, true))
            await using (TarWriter writer = new(gzip)) {
                const string content = "fake sdk content";

                PaxTarEntry entry = new(TarEntryType.RegularFile, "sdk/testfile.txt") {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };

                await writer.WriteEntryAsync(entry, TestContext.Current.CancellationToken);
            }

            fileContent = ms.ToArray();
        }

        var hash = SHA512.HashData(fileContent);

        DotnetFile sdkFile = new() {
            Name = "dotnet-sdk-linux-x64.tar.gz",
            Url = sdkUrl,
            Sha512Hash = hash,
            RuntimeIdentifier = "linux-x64"
        };

        DotupRegistry registry = new();

        // Mock HttpClient
        using HttpResponseMessage responseMessage = new();
        responseMessage.StatusCode = HttpStatusCode.OK;
        responseMessage.Content = new ByteArrayContent(fileContent);

        Mock<HttpMessageHandler> handlerMock = new();

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        using HttpClient httpClient = new(handlerMock.Object);

        // Mock TarExtractor
        tarExtractorMock.Setup(t => t.ExtractToDirectoryAsync(It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<Stream, string, bool, CancellationToken>((_, dest, _, _) => {
                // Simulate extraction by creating the expected file in MockFileSystem
                var filePath = Path.Combine(dest, "sdk", "testfile.txt");
                fileSystem.Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                fileSystem.File.WriteAllText(filePath, "fake sdk content");
            })
            .Returns(Task.CompletedTask);

        // Act
        var result = await service.InstallSdkAsync(httpClient, "10.0", fileContent.Length, sdkFile, registry, sdkVersion, runtimeVersion,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result);

        // Check if file was extracted
        var expectedFilePath = Path.Combine(TestRoot, "dotnetroot", "sdk", "testfile.txt");

        Assert.True(fileSystem.FileExists(expectedFilePath));

        registryServiceMock.Verify(r => r.AddOrModifyRegistryAsync(It.IsAny<string>(),
            It.IsAny<DotupRegistry>(),
            It.IsAny<SemVersion>(),
            It.IsAny<SemVersion>(),
            It.IsAny<SemVersion[]>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InstallSdkAsync_ShouldReturnFalse_WhenHashMismatch() {
        // Arrange
        var sdkVersion = SemVersion.Parse("10.0.100");
        var runtimeVersion = SemVersion.Parse("10.0.0");
        Uri sdkUrl = new("https://example.com/sdk.tar.gz");
        byte[] fileContent = [1, 2, 3, 4, 5]; // Dummy content
        var wrongHash = new byte[64]; // Empty hash, definitely wrong

        DotnetFile sdkFile = new() {
            Name = "dotnet-sdk-linux-x64.tar.gz",
            Url = sdkUrl,
            Sha512Hash = wrongHash, // Inject wrong hash
            RuntimeIdentifier = "linux-x64"
        };

        using HttpResponseMessage responseMessage = new();
        responseMessage.StatusCode = HttpStatusCode.OK;
        responseMessage.Content = new ByteArrayContent(fileContent);

        Mock<HttpMessageHandler> handlerMock = new();

        handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(responseMessage);

        using HttpClient httpClient = new(handlerMock.Object);

        // Act
        var result = await service.InstallSdkAsync(httpClient, "10.0", fileContent.Length, sdkFile, new(), sdkVersion, runtimeVersion,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result); // Should fail
        Assert.False(fileSystem.Directory.Exists(Path.Combine(TestRoot, "temp"))); // Temp dir should be cleaned up

        registryServiceMock.Verify(r => r.AddOrModifyRegistryAsync(It.IsAny<string>(), It.IsAny<DotupRegistry>(), It.IsAny<SemVersion>(),
            It.IsAny<SemVersion>(),
            It.IsAny<SemVersion[]>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UninstallSdkAsync_ShouldDeleteFiles_WhenNoOtherReferences() {
        // Arrange
        var sdkVersion = SemVersion.Parse("10.0.100");
        var runtimeVersion = SemVersion.Parse("10.0.0");
        const string channel = "10.0";

        // Setup Registry with only ONE channel using this SDK
        DotupRegistry registry = new() {
            InstalledChannels = {
                [channel] = new() {
                    SdkVersion = sdkVersion,
                    RuntimeVersion = runtimeVersion,
                    SdkManifests = []
                }
            }
        };

        // Setup Files
        var sdkPath = Path.Combine(TestRoot, "dotnetroot", "sdk", sdkVersion.ToString());
        var runtimePath = Path.Combine(TestRoot, "dotnetroot", "shared", "Microsoft.NETCore.App", runtimeVersion.ToString());

        fileSystem.Directory.CreateDirectory(sdkPath);
        fileSystem.Directory.CreateDirectory(runtimePath);

        // Act
        await service.UninstallSdkAsync(channel, registry, true, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(fileSystem.Directory.Exists(sdkPath)); // SDK should be gone
        Assert.False(fileSystem.Directory.Exists(runtimePath)); // Runtime should be gone (no other refs)
        registryServiceMock.Verify(r => r.RemoveFromRegistryAsync(channel, registry, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UninstallSdkAsync_ShouldKeepSharedRuntime_WhenOtherChannelUsesIt() {
        // Arrange
        var sdkVersionToRemove = SemVersion.Parse("10.0.100");
        var sdkVersionToKeep = SemVersion.Parse("10.0.200");
        var sharedRuntimeVersion = SemVersion.Parse("10.0.0");

        const string channelToRemove = "10.0";
        const string channelToKeep = "10.0-preview";

        // Setup Registry: Two channels share the same RuntimeVersion
        DotupRegistry registry = new() {
            InstalledChannels = {
                [channelToRemove] = new() {
                    SdkVersion = sdkVersionToRemove,
                    RuntimeVersion = sharedRuntimeVersion,
                    SdkManifests = []
                },
                [channelToKeep] = new() {
                    SdkVersion = sdkVersionToKeep,
                    RuntimeVersion = sharedRuntimeVersion, // Same runtime!
                    SdkManifests = []
                }
            }
        };

        // Setup Files
        var sdkPathToRemove = Path.Combine(TestRoot, "dotnetroot", "sdk", sdkVersionToRemove.ToString());
        var sdkPathToKeep = Path.Combine(TestRoot, "dotnetroot", "sdk", sdkVersionToKeep.ToString());
        var sharedRuntimePath = Path.Combine(TestRoot, "dotnetroot", "shared", "Microsoft.NETCore.App", sharedRuntimeVersion.ToString());

        fileSystem.Directory.CreateDirectory(sdkPathToRemove);
        fileSystem.Directory.CreateDirectory(sdkPathToKeep);
        fileSystem.Directory.CreateDirectory(sharedRuntimePath);

        // Act
        await service.UninstallSdkAsync(channelToRemove, registry, true, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(fileSystem.Directory.Exists(sdkPathToRemove)); // Target SDK gone
        Assert.True(fileSystem.Directory.Exists(sdkPathToKeep)); // Other SDK stays
        Assert.True(fileSystem.Directory.Exists(sharedRuntimePath)); // Shared Runtime MUST stay

        registryServiceMock.Verify(r => r.RemoveFromRegistryAsync(channelToRemove, registry, It.IsAny<CancellationToken>()), Times.Once);
    }
}
