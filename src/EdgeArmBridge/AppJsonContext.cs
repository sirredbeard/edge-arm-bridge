using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeArmBridge;

// Source-generated JSON metadata. The reflection based serializer calls
// this replaces are the one thing in the service the trimmer and the AOT
// compiler cannot follow, so every type that crosses JsonSerializer gets
// listed here instead.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SyncConfig))]
[JsonSerializable(typeof(StatusFile))]
[JsonSerializable(typeof(StateFile))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
