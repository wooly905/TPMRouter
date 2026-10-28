using System.Text.Json;
using System.Text.Json.Nodes;

namespace TPMRouter.Api.Relay;

/// <summary>
/// Drops reasoning ciphertext so a later turn can move to another Azure resource.
/// The ciphertext is produced for one resource and cannot be verified by another.
/// </summary>
public static class EncryptedReasoning
{
    public const string IncludeValue = "reasoning.encrypted_content";

    public static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("encrypted_content");
                if (obj["include"] is JsonArray include)
                {
                    for (int i = include.Count - 1; i >= 0; i--)
                    {
                        if (include[i] is JsonValue value
                            && value.TryGetValue(out string? item)
                            && item == IncludeValue)
                        {
                            include.RemoveAt(i);
                        }
                    }

                    if (include.Count == 0)
                    {
                        obj.Remove("include");
                    }
                }

                foreach (KeyValuePair<string, JsonNode?> property in obj.ToArray())
                {
                    Strip(property.Value);
                }

                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    Strip(item);
                }

                break;
        }
    }

    /// <summary>Returns <paramref name="json"/> unchanged when it has no encrypted reasoning.</summary>
    public static string StripJson(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (node is null)
        {
            return json;
        }

        JsonNode snapshot = node.DeepClone();
        Strip(node);
        return JsonNode.DeepEquals(snapshot, node) ? json : node.ToJsonString();
    }
}
