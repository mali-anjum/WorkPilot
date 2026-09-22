using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

public class ModalTests : TestContext
{
    public ModalTests()
    {
        // Loose mode: unconfigured JS calls (import, trapFocus, dispose) return
        // defaults instead of throwing, so the component's interop calls don't
        // need a full JS runtime to exercise its Blazor-level behavior.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Escape_raises_OnClose_and_closes_the_modal()
    {
        var closed = false;
        var cut = RenderComponent<Modal>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.Title, "Example")
            .Add(p => p.OnClose, () => closed = true)
            .AddChildContent("<p>Body</p>"));

        cut.Find("[role='dialog']").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.True(closed);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[role='dialog']")));
    }

    [Fact]
    public void Renders_as_an_accessible_dialog_when_open()
    {
        var cut = RenderComponent<Modal>(parameters => parameters
            .Add(p => p.IsOpen, true)
            .Add(p => p.Title, "Example")
            .AddChildContent("<p>Body</p>"));

        var dialog = cut.Find("[role='dialog']");
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
    }

    [Fact]
    public void Renders_nothing_when_closed()
    {
        var cut = RenderComponent<Modal>(parameters => parameters
            .Add(p => p.IsOpen, false)
            .Add(p => p.Title, "Example"));

        Assert.Empty(cut.FindAll("[role='dialog']"));
    }
}
