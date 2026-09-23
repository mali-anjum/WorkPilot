using System.Text.Json;
using WorkPilot.Domain.Modules.Agent;

namespace WorkPilot.Application.Modules.Agent;

/// <summary>Structural check only (spec 0005's confirmed choice, not an LLM self critique): did the tool report success, and does its output carry every field it declared.</summary>
public interface IVerificationEngine
{
    bool Verify(ITool tool, ToolExecutionResult result);
}

public sealed class VerificationEngine : IVerificationEngine
{
    public bool Verify(ITool tool, ToolExecutionResult result)
    {
        if (!result.Success)
        {
            return false;
        }

        if (tool.ExpectedOutputFields.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(result.OutputJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(result.OutputJson);
            return tool.ExpectedOutputFields.All(field => document.RootElement.TryGetProperty(field, out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
