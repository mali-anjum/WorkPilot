using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

public class CommandPaletteTests : TestContext
{
    public CommandPaletteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Escape_closes_the_palette()
    {
        var cut = RenderComponent<CommandPalette>(parameters => parameters
            .Add(p => p.IsOpen, true));

        cut.Find("[role='dialog']").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role='dialog']")));
    }

    [Fact]
    public void OnShortcut_opens_the_palette()
    {
        var cut = RenderComponent<CommandPalette>(parameters => parameters
            .Add(p => p.IsOpen, false));

        cut.InvokeAsync(() => cut.Instance.OnShortcut());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='dialog']")));
    }

    [Fact]
    public void Renders_a_search_input_when_open()
    {
        var cut = RenderComponent<CommandPalette>(parameters => parameters
            .Add(p => p.IsOpen, true));

        Assert.NotNull(cut.Find("input[type='text']"));
    }
}
