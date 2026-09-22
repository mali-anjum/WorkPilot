using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

public class ButtonTests : TestContext
{
    [Fact]
    public void Click_raises_OnClick()
    {
        var clicked = false;
        var cut = RenderComponent<Button>(parameters => parameters
            .Add(p => p.OnClick, () => clicked = true)
            .AddChildContent("Click me"));

        cut.Find("button").Click();

        Assert.True(clicked);
    }

    [Fact]
    public void Disabled_button_has_the_disabled_attribute()
    {
        var cut = RenderComponent<Button>(parameters => parameters
            .Add(p => p.Disabled, true)
            .AddChildContent("Click me"));

        Assert.NotNull(cut.Find("button").GetAttribute("disabled"));
    }

    // Enter/Space activating a focused native <button> is browser-native keyboard
    // behavior (no @onkeydown wiring in this component to intercept), which bUnit's
    // headless DOM cannot exercise; verified manually in /check verify instead.
}
