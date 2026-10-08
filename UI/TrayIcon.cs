using System.Reflection;
using TwitchDropsMiner.Core;
using Forms = System.Windows.Forms;

namespace TwitchDropsMiner.UI;

/// <summary>Иконка в системном трее (WinForms NotifyIcon) с меню и уведомлениями.</summary>
public sealed class TrayIcon : IDisposable
{
    private const string Title = "Twitch Drops Miner";
    private readonly Forms.NotifyIcon _icon;
    private readonly Dictionary<TrayIconState, System.Drawing.Icon> _icons = new();

    public event Action? ShowRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var state in Enum.GetValues<TrayIconState>())
        {
            using var s = asm.GetManifestResourceStream($"Icons.{state.ToString().ToLowerInvariant()}.ico");
            if (s is not null) _icons[state] = new System.Drawing.Icon(s);
        }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("gui.tray.show", "Show"), null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("gui.tray.quit", "Quit"), null, (_, _) => QuitRequested?.Invoke());
        _icon = new Forms.NotifyIcon
        {
            Icon = _icons.GetValueOrDefault(TrayIconState.Pickaxe),
            Text = Title,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowRequested?.Invoke(); };
    }

    public void SetState(TrayIconState state)
    {
        if (_icons.TryGetValue(state, out var icon)) _icon.Icon = icon;
    }

    public void Notify(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(10_000);
    }

    private static string Shorten(string text, int byLen, int minLen)
    {
        if (text.Length <= minLen + 3 || byLen <= 0) return text;
        return text[..^Math.Min(byLen + 3, text.Length - minLen)] + "...";
    }

    /// <summary>Подсказка при наведении: игра, награда и прогресс (Windows ограничивает её 127 символами).</summary>
    public void UpdateTitle(TimedDrop? drop)
    {
        if (drop is null) { _icon.Text = Title; return; }
        var c = drop.Campaign;
        var parts = new[]
        {
            $"{Title}\n",
            $"{c.Game.Name}\n",
            drop.RewardsText(),
            $" {drop.Progress:P1} ({c.ClaimedDrops}/{c.TotalDrops})",
        };
        const int maxLen = 127, minLen = 30;
        int missing = parts.Sum(p => p.Length) - maxLen;
        if (missing > 0) { parts[2] = Shorten(parts[2], missing, minLen); missing = parts.Sum(p => p.Length) - maxLen; }
        if (missing > 0) { parts[1] = Shorten(parts[1], missing, minLen); missing = parts.Sum(p => p.Length) - maxLen; }
        var text = string.Concat(parts);
        _icon.Text = text.Length > maxLen ? text[..maxLen] : text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }
}
