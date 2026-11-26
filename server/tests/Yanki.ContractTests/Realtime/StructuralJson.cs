using System.Text.Json;

namespace Yanki.ContractTests.Realtime;

/// <summary>
/// Structural comparison used by the golden fixtures: the expectation lists the fields that matter,
/// the actual frame may carry more (forward compatibility), and the placeholder <c>"…"</c> asserts
/// presence only — for values the server generates, such as UUIDv7 ids and timestamps.
/// </summary>
internal static class StructuralJson
{
    /// <summary>The presence-only placeholder, U+2026 HORIZONTAL ELLIPSIS.</summary>
    public const string Placeholder = "…";

    public static void AssertMatches(JsonElement expected, JsonElement actual, string path = "$")
    {
        if (IsPlaceholder(expected))
        {
            Assert.False(
                actual.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null,
                $"{path}: expected any value to be present but found {actual.ValueKind}.");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Assert.True(actual.ValueKind == JsonValueKind.Object, $"{path}: expected an object but found {actual.ValueKind}.");
                foreach (JsonProperty property in expected.EnumerateObject())
                {
                    Assert.True(
                        actual.TryGetProperty(property.Name, out JsonElement actualValue),
                        $"{path}.{property.Name} is missing from the received frame: {actual.GetRawText()}");
                    AssertMatches(property.Value, actualValue, $"{path}.{property.Name}");
                }

                break;

            case JsonValueKind.Array:
                Assert.True(actual.ValueKind == JsonValueKind.Array, $"{path}: expected an array but found {actual.ValueKind}.");
                Assert.True(
                    expected.GetArrayLength() == actual.GetArrayLength(),
                    $"{path}: expected {expected.GetArrayLength()} items but found {actual.GetArrayLength()}.");
                for (int i = 0; i < expected.GetArrayLength(); i++)
                {
                    AssertMatches(expected[i], actual[i], $"{path}[{i}]");
                }

                break;

            default:
                Assert.True(expected.ValueKind == actual.ValueKind, $"{path}: expected {expected.ValueKind} but found {actual.ValueKind}.");
                Assert.True(
                    expected.GetRawText() == actual.GetRawText(),
                    $"{path}: expected {expected.GetRawText()} but found {actual.GetRawText()}.");
                break;
        }
    }

    private static bool IsPlaceholder(JsonElement expected) =>
        expected.ValueKind == JsonValueKind.String && expected.ValueEquals(Placeholder);
}
