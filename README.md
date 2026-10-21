# Brim · Media+

Plugin für [Brim](https://github.com/wheatley2056/brim), die Notch für Windows. Brim zeigt schon selbst, was läuft (Cover, Steuerung, Ausgabegerät, Mixer). Media+ legt dazu:

| Was | Wo |
|---|---|
| **Songtext, der mitläuft** | Widget **Songtext** auf „Jetzt“ (mittel und breit: aktuelle und nächste Zeile; groß und voll: mit Zeile davor und zwei danach). Im Tab **Plugins** mehr Zeilen; ein Klick auf eine Zeile springt dorthin. Texte ohne Zeitstempel erscheinen als scrollbare Liste. |
| **Musiklautstärke** | Widget **Musiklautstärke**: Schieber und Stummschalten nur für die App, die gerade spielt, unabhängig von der Systemlautstärke. |
| **Als Nächstes** (Spotify, optional) | Widget **Als Nächstes**: die nächsten Titel der Spotify-Warteschlange mit Cover. Braucht eine einmalige Anmeldung, siehe unten. |

Den Titel liest Media+ wie Brim über die Windows-Medienschnittstelle. Songtexte und Lautstärke gehen deshalb mit jedem Player, der sich dort meldet: Spotify, YouTube Music oder YouTube im Browser, Apple Music, Tidal, VLC und andere.

Widgets hinzufügen: „Jetzt“ lange drücken oder rechts anklicken, dann „+“.

## Installieren

Am einfachsten über die Plugin-Bibliothek ab Brim 2.3: Einstellungen → Plugins → Bibliothek → **Media+** → Installieren.

Von Hand aus dem Quellcode (Brim liegt als Repository daneben):

```powershell
git clone https://github.com/wheatley2056/brim.git
git clone https://github.com/wheatley2056/brim-media.git
cd brim-media
dotnet publish src/BrimMedia -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Brim\Plugins\media"
```

Danach Brim neu starten, Notch aufklappen, Tab **Plugins**: rechts **Media+** einschalten.

Rechte: **Mediensteuerung** (Titel lesen, zu einer Songtext-Zeile springen) und **Netzwerk** (Songtexte, Spotify).

## Songtexte

Kommen von [LRCLIB](https://lrclib.net), einer freien Songtext-Datenbank ohne Konto. Dafür gehen Titel, Interpret, Album und Länge des laufenden Songs an lrclib.net. Wer das nicht möchte, schaltet in den Plugin-Einstellungen **Songtexte laden** aus.

## Spotify verbinden

Die Warteschlange gibt es nicht über Windows, nur über die Spotify-Web-API. Spotify lässt neue Entwickler-Apps nur für wenige freigeschaltete Nutzer zu, darum nutzt jeder seine eigene, kostenlose App:

1. Auf [developer.spotify.com/dashboard](https://developer.spotify.com/dashboard) anmelden und **Create app** wählen.
2. Name und Beschreibung beliebig, z. B. „Brim“. Bei **Redirect URIs** genau `http://127.0.0.1:47823/callback` eintragen und hinzufügen. Bei den APIs **Web API** ankreuzen. Speichern.
3. In der App unter **Settings** die **Client ID** kopieren.
4. In Brim: Einstellungen → Plugins → Media+ → **Spotify-Client-ID** einfügen.
5. Im Widget **Als Nächstes** oder im Tab **Plugins** auf **Verbinden**. Der Browser öffnet die Anmeldung bei Spotify; danach steht dort „Mit Spotify verbunden“.

Media+ fragt nur Leserechte an (`user-read-playback-state`, `user-read-currently-playing`) und nutzt PKCE, also kein Client-Secret. Die Anmeldung liegt mit Windows (DPAPI) verschlüsselt in `%LocalAppData%\Brim\PluginData\de.brim.media\`. **Spotify trennen** im Tab **Plugins** löscht sie; widerrufen lässt sie sich auch unter [spotify.com/account/apps](https://www.spotify.com/account/apps/).

## Einstellungen

| Einstellung | Standard | |
|---|---|---|
| Songtexte von lrclib.net laden | an | |
| Spotify-Client-ID | leer | für die Warteschlange |
| Titel in der Warteschlange | 5 | im Tab **Plugins** (1–10) |

## Entwickeln

```powershell
dotnet test tests/BrimMedia.Tests
```

`src/BrimMedia/Core` ist plattformneutral (Songtexte, Spotify, Ansichten, Logik) und wird von den Tests auch unter Linux gebaut. `src/BrimMedia/Windows` spricht die Medienschnittstelle (SMTC), Core Audio (NAudio) und DPAPI.

## Release

Tag `v<Version>` pushen (gleich der `version` in `src/BrimMedia/plugin.json`). Die Action baut, testet und hängt `brim-media.zip` ans Release. Danach im Brim-Repository:

```powershell
./build/catalog/Add-Plugin.ps1 -Repository wheatley2056/brim-media -Tag v1.0.0 -Summary "…" -Author "Brim" -Categories Musik
```

## Lizenz

GPL-3.0-or-later, wie Brim.
