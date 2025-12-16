using Dotup.Json.Registry;
using System.IO.Abstractions;

namespace Dotup.Services;

internal sealed class RegistryService(IFileSystem fileSystem) : IRegistryService {
    public async Task<DotupRegistry?> GetRegistryAsync(CancellationToken token = default) {
        var registryPath = RegistryJsonPath;

        if (!fileSystem.File.Exists(registryPath)) {
            throw new FileNotFoundException($"Error: Registry file '{registryPath}' not found.");
        }

        try {
            await using var fs = fileSystem.FileStream.New(registryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

            var registry = await JsonSerializer.DeserializeAsync(fs, Djsc.DotupRegistry, token)
                ?? throw new InvalidOperationException($"Error: Registry file '{registryPath}' is empty or contains invalid data.");

            return registry;
        } catch (FileNotFoundException) {
            throw new FileNotFoundException($"Error: Registry file '{registryPath}' not found.");
        } catch (IOException) {
            throw new IOException($"Error: Could not open '{registryPath}'. The file might be in use by another process.");
        } catch (JsonException) {
            throw new JsonException($"Error: '{registryPath}' is corrupted or contains invalid JSON.");
        }
    }

    public async Task<bool> IsChannelAlreadyInstalledAsync(string channel, CancellationToken token = default) {
        if (!fileSystem.File.Exists(RegistryJsonPath)) {
            return false;
        }

        var registry = await GetRegistryAsync(token);

        return registry?.InstalledChannels.ContainsKey(channel) ?? false;
    }

    public async Task AddOrModifyRegistryAsync(
        string channel,
        DotupRegistry registry,
        SemVersion sdkVersion,
        SemVersion runtimeVersion,
        SemVersion[] sdkManifests,
        bool inputIsGreater,
        CancellationToken token = default) {
        var channels = registry.InstalledChannels;

        if (!channels.TryGetValue(channel, out var value)) {
            channels[channel] = new() {
                SdkVersion = sdkVersion,
                RuntimeVersion = runtimeVersion,
                SdkManifests = sdkManifests
            };
        } else {
            value.SdkVersion = sdkVersion;
            value.RuntimeVersion = runtimeVersion;
            value.SdkManifests = sdkManifests;
        }

        if (inputIsGreater) {
            registry.CliVersion = sdkVersion;
        }

        await SaveRegistryAsync(registry);
    }

    public async Task RemoveFromRegistryAsync(string channel, DotupRegistry registry, CancellationToken token = default) {
        registry.InstalledChannels.Remove(channel);

        await SaveRegistryAsync(registry);
    }

    private async Task SaveRegistryAsync(DotupRegistry registry) {
        await using var fs = fileSystem.FileStream.New(RegistryJsonPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
        await using Utf8JsonWriter writer = new(fs, IndentedWriterOptions);

        JsonSerializer.Serialize(writer, registry, Djsc.DotupRegistry);
    }
}
