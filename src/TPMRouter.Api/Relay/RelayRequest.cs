using System.Text.Json.Nodes;

namespace TPMRouter.Api.Relay;

public abstract record RelayRequest(JsonObject Body, string Model, bool Stream, int? MaxOutputTokens, bool HasImages)
{
    public virtual string OutputLimitField => "max_output_tokens";
    public virtual int ChoiceCount => 1;
}
