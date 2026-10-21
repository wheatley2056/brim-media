using Brim.Core;
using Brim.Core.Plugins;
using Brim.Core.Widgets;
using Brim.Sdk;

namespace BrimMedia.Core;

/// <summary>Was das Plugin bei Brim anzeigt. Getrennt von <see cref="BrimPlugin"/>, damit Tests mitschreiben können.</summary>
public interface INotchSink
{
    Task ShowHudAsync(HudParams hud);
    Task SetViewAsync(string id, Widget content, int order);
    Task LogAsync(string message, string level);
    Task SetWidgetAsync(string id, TileSize size, Widget content);
}

public sealed class BrimSink(BrimPlugin brim) : INotchSink
{
    public Task ShowHudAsync(HudParams hud) => brim.ShowHudAsync(hud);
    public Task SetViewAsync(string id, Widget content, int order) => brim.SetViewAsync(id, content, order);
    public Task LogAsync(string message, string level) => brim.LogAsync(message, level);
    public Task SetWidgetAsync(string id, TileSize size, Widget content) => brim.SetWidgetAsync(id, size, content);
}
