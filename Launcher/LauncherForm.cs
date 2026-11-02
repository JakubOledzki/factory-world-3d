using System.Reflection;

namespace FactoryWorld.Launcher;

/// <summary>The launcher's window: checks for a new version, downloads what changed, then "Graj" starts the game.</summary>
public sealed class LauncherForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 26, 30);
    private static readonly Color Panel = Color.FromArgb(36, 39, 45);
    private static readonly Color Accent = Color.FromArgb(240, 176, 64);
    private static readonly Color Ink = Color.FromArgb(232, 232, 232);
    private static readonly Color Muted = Color.FromArgb(150, 154, 160);

    private readonly Label status = new() { AutoSize = false, ForeColor = Ink, Font = new Font("Segoe UI", 11f) };
    private readonly Label detail = new() { AutoSize = false, ForeColor = Muted, Font = new Font("Segoe UI", 9f) };
    private readonly ProgressBar bar = new() { Style = ProgressBarStyle.Continuous, Maximum = 1000 };
    private readonly Button play = new() { Text = "Graj", Enabled = false, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 12f) };
    private readonly Button retry = new() { Text = "Spróbuj ponownie", Visible = false, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9.5f) };
    private readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Uri baseUrl;
    private readonly string serverUrl;
    private readonly Updater updater;
    private readonly CancellationTokenSource closing = new();
    private Manifest? manifest;
    private DateTime speedFrom = DateTime.UtcNow;
    private long speedBytes;

    private readonly bool autoPlay;

    public LauncherForm(string gameDirectory, bool autoPlay)
    {
        this.autoPlay = autoPlay;
        baseUrl = new Uri(Setting("LauncherBaseUrl"));
        serverUrl = Setting("GameServerUrl");
        updater = new Updater(http, baseUrl, gameDirectory);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"FactoryWorld3D-Launcher/{SelfUpdate.Current}");

        Text = "Factory World 3D";
        ClientSize = new Size(560, 250);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        Font = new Font("Segoe UI", 9f);

        var title = new Label { Text = "FACTORY WORLD 3D", ForeColor = Ink, Font = new Font("Segoe UI Black", 20f), AutoSize = true, Location = new Point(24, 20) };
        var version = new Label { Text = $"Launcher {SelfUpdate.Current.ToString(3)}", ForeColor = Muted, AutoSize = true, Location = new Point(28, 62) };
        status.SetBounds(24, 100, 512, 24);
        bar.SetBounds(24, 130, 512, 14);
        detail.SetBounds(24, 150, 512, 20);
        play.SetBounds(376, 190, 160, 42);
        play.BackColor = Accent;
        play.ForeColor = Color.FromArgb(20, 20, 20);
        play.FlatAppearance.BorderSize = 0;
        retry.SetBounds(24, 196, 160, 32);
        retry.BackColor = Panel;
        retry.ForeColor = Ink;
        retry.FlatAppearance.BorderColor = Muted;
        Controls.AddRange([title, version, status, bar, detail, play, retry]);

        play.Click += (_, _) => Play();
        retry.Click += async (_, _) => await UpdateAsync();
        Shown += async (_, _) => await UpdateAsync();
        FormClosing += (_, _) => closing.Cancel();
    }

    private static string Setting(string key) =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == key).Value
            ?? throw new InvalidOperationException(key);

    private async Task UpdateAsync()
    {
        retry.Visible = false;
        play.Enabled = false;
        var progress = new Progress<UpdateProgress>(Show);
        try
        {
            status.Text = "Łączę z serwerem…";
            manifest = await updater.ManifestAsync(closing.Token);
            if (SelfUpdate.IsNewer(manifest.Launcher))
            {
                status.Text = "Aktualizuję launcher…";
                if (await SelfUpdate.ApplyAsync(http, baseUrl, manifest.Launcher!, closing.Token))
                {
                    Close();
                    return;
                }
            }
            var missing = await updater.MissingAsync(manifest, progress, closing.Token);
            if (missing.Count > 0)
            {
                speedFrom = DateTime.UtcNow;
                speedBytes = 0;
                await updater.SyncAsync(manifest, missing, progress, closing.Token);
            }
            else
            {
                await updater.SyncAsync(manifest, missing, new Progress<UpdateProgress>(_ => { }), closing.Token);
            }
            status.Text = $"Gra jest aktualna (wersja {manifest.Version})";
            detail.Text = "";
            bar.Value = bar.Maximum;
            play.Enabled = true;
            play.Focus();
            if (autoPlay)
            {
                Play();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            status.Text = "Nie udało się zaktualizować gry";
            detail.Text = e is HttpRequestException ? "Brak połączenia z serwerem. Sprawdź internet i spróbuj ponownie." : e.Message;
            retry.Visible = true;
            // A game already installed can still be played
            play.Enabled = manifest is not null && File.Exists(Path.Combine(updater.GameDirectory, manifest.Exe.Replace('/', '\\')));
        }
    }

    private void Show(UpdateProgress p)
    {
        status.Text = p.Status;
        bar.Value = p.Total > 0 ? (int)Math.Clamp(p.Done * bar.Maximum / p.Total, 0, bar.Maximum) : 0;
        if (p.Status.StartsWith("Pobieram", StringComparison.Ordinal))
        {
            var seconds = Math.Max(0.5, (DateTime.UtcNow - speedFrom).TotalSeconds);
            speedBytes = p.Done;
            var speed = speedBytes / seconds;
            var left = speed > 0 ? TimeSpan.FromSeconds((p.Total - p.Done) / speed) : TimeSpan.Zero;
            detail.Text = $"{Mb(p.Done)} z {Mb(p.Total)} MB · {Mb((long)speed)} MB/s · zostało ok. {(left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes} min" : $"{left.Seconds} s")}";
        }
        else
        {
            detail.Text = "";
        }
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private void Play()
    {
        if (manifest is null)
        {
            return;
        }
        try
        {
            updater.Start(manifest, serverUrl);
            Close();
        }
        catch (Exception e)
        {
            MessageBox.Show(this, $"Nie udało się uruchomić gry: {e.Message}", "Factory World 3D", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
