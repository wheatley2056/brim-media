using System.Collections.Concurrent;
using System.Diagnostics;
using BrimMedia.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BrimMedia.Windows;

/// <summary>
/// Lautstärke der Audiositzungen des Players auf dem Standardgerät, über NAudio (Core Audio).
/// Wie in Brims AudioService leben alle Core-Audio-Objekte auf einem eigenen MTA-Thread,
/// und niemand wartet länger als <see cref="CallTimeout"/> auf ihn.
/// </summary>
public sealed class CoreAudioVolumes : IAppVolumes, IDisposable
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Dictionary<uint, (string Process, string Name)> _names = [];
    private readonly Thread _thread;
    private MMDeviceEnumerator? _enumerator;

    public CoreAudioVolumes()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Media+ Audio" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public AppVolume? Read(string sourceAppId) => Call(() =>
        {
            var sessions = Sessions(sourceAppId);
            if (sessions.Count == 0)
            {
                return null;
            }
            var name = sessions[0].Name;
            var volume = sessions.Max(s => s.Session.SimpleAudioVolume.Volume);
            var muted = sessions.All(s => s.Session.SimpleAudioVolume.Mute);
            return new AppVolume(name, volume, muted);
        }, null);

    public void Set(string sourceAppId, float volume) => Post(() =>
        {
            foreach (var (session, _) in Sessions(sourceAppId))
            {
                session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1);
                if (volume > 0)
                {
                    session.SimpleAudioVolume.Mute = false;
                }
            }
        });

    public void ToggleMute(string sourceAppId) => Post(() =>
        {
            var sessions = Sessions(sourceAppId);
            var mute = !sessions.All(s => s.Session.SimpleAudioVolume.Mute);
            sessions.ForEach(s => s.Session.SimpleAudioVolume.Mute = mute);
        });

    private List<(AudioSessionControl Session, string Name)> Sessions(string sourceAppId)
    {
        var result = new List<(AudioSessionControl, string)>();
        _enumerator ??= new MMDeviceEnumerator();
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired)
            {
                continue;
            }
            var (process, name) = NameOf(session.GetProcessID);
            if (AudioMatch.Matches(sourceAppId, process))
            {
                result.Add((session, name));
            }
        }
        return result;
    }

    private (string Process, string Name) NameOf(uint pid)
    {
        if (_names.TryGetValue(pid, out var cached))
        {
            return cached;
        }
        var entry = ("", $"Prozess {pid}");
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            try
            {
                if (process.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } description)
                {
                    name = description;
                }
            }
            catch (Exception)
            {
                // Kein Zugriff auf das Modul (andere Integritätsstufe): Prozessname genügt.
            }
            entry = (process.ProcessName, name);
        }
        catch (ArgumentException)
        {
            // Prozess schon beendet
        }
        if (_names.Count > 200)
        {
            _names.Clear();
        }
        _names[pid] = entry;
        return entry;
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Core Audio: {ex.Message}");
            }
        }
    }

    private void Post(Action work)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(work);
        }
    }

    private T Call<T>(Func<T> work, T fallback)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        try
        {
            return done.Task.Wait(CallTimeout) ? done.Task.Result : fallback;
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"Core Audio: {ex.InnerException?.Message}");
            return fallback;
        }
    }

    public void Dispose()
    {
        Post(() => _enumerator?.Dispose());
        _queue.CompleteAdding();
        _thread.Join(CallTimeout);
    }
}
