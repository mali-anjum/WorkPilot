using Microsoft.JSInterop;

namespace WorkPilot.Web.Features.Common;

/// <summary>
/// The browser's IANA time zone, read once per circuit through JS
/// (<c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>), so server rendered times can show in
/// the viewer's local time (spec 0011). UTC until it is known, or when the browser gives none.
/// Call <see cref="GetAsync"/> from <c>OnAfterRenderAsync</c>: JS interop is not available while prerendering.
/// </summary>
public sealed class BrowserTimeZone(IJSRuntime js)
{
    private TimeZoneInfo? _zone;

    /// <summary>The zone once read, else UTC.</summary>
    public TimeZoneInfo Current => _zone ?? TimeZoneInfo.Utc;

    /// <summary>Whether the browser's zone has been read for this circuit.</summary>
    public bool IsKnown => _zone is not null;

    /// <summary>Reads the browser's zone the first time it is called; later calls return the cached one.</summary>
    public async Task<TimeZoneInfo> GetAsync()
    {
        if (_zone is not null)
        {
            return _zone;
        }

        try
        {
            await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");
            var id = await module.InvokeAsync<string?>("timeZone");
            _zone = !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
        }
        catch (JSException)
        {
            _zone = TimeZoneInfo.Utc;
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; nothing will render with it, so do not cache.
        }
        catch (InvalidOperationException)
        {
            // Prerendering: no JS yet; the interactive render asks again.
        }

        return Current;
    }
}
