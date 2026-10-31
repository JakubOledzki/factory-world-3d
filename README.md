# Factory World 3D

Trójwymiarowy klient gry [Factory World](https://github.com/JakubOledzki/factory-world): budowanie fabryk z perspektywy pierwszej osoby, z realistyczną grafiką w Unreal Engine 5.

Cała logika gry działa na serwerze Factory World (C#/.NET, PostgreSQL), tym samym, z którego korzysta wersja 2D. Ten projekt jest tylko nowym klientem: łączy się z serwerem przez WebSocket i mówi tym samym protokołem JSON (opis w README repozytorium `factory-world`, sekcja „Protokół”).

## Wymagania

- Unreal Engine **5.8** (Epic Games Launcher)
- Visual Studio 2022 z zestawami „Game development with C++” i „Desktop development with C++”, Windows SDK i .NET Framework 4.6.2 targeting pack
- Git LFS (`git lfs install`): modele, tekstury, mapy i dźwięki są trzymane w LFS
- Karta graficzna z obsługą DirectX 12 (SM6). Sprzętowy ray tracing (np. RTX) jest włączony, a bez niego Lumen przechodzi na śledzenie programowe

## Uruchomienie

1. Sklonuj repozytorium i pobierz pliki LFS:
   ```
   git clone https://github.com/JakubOledzki/factory-world-3d.git
   cd factory-world-3d
   git lfs pull
   ```
2. Kliknij prawym przyciskiem `FactoryWorld3D.uproject` → **Generate Visual Studio project files**.
3. Otwórz `FactoryWorld3D.sln` i zbuduj konfigurację **Development Editor / Win64** albo po prostu otwórz `FactoryWorld3D.uproject` (edytor sam zaproponuje kompilację).

Kompilacja z wiersza poleceń:

```
"C:\Program Files\Epic Games\UE_5.8\Engine\Build\BatchFiles\Build.bat" FactoryWorld3DEditor Win64 Development -Project="%CD%\FactoryWorld3D.uproject" -WaitMutex
```

## Grafika

Projekt jest ustawiony na najwyższą jakość obrazu (`Config/DefaultEngine.ini`):

- Lumen: dynamiczne oświetlenie globalne i odbicia, sprzętowy ray tracing
- Nanite i wirtualne mapy cieni (Virtual Shadow Maps)
- TSR (Temporal Super Resolution)
- niebo fizyczne (Sky Atmosphere), chmury wolumetryczne i mgła wolumetryczna
- domyślna skalowalność: Epic

## Zasoby

Używamy wyłącznie własnych modeli (Blender, skrypty w Pythonie) oraz darmowych zasobów z licencją pozwalającą na użycie komercyjne (np. Poly Haven, ambientCG – CC0; Fab – tylko z licencją komercyjną). Żadnych zasobów, nazw ani treści z innych gier.

## Połączenie z serwerem

Klient łączy się z serwerem Factory World pod adresem z `Config/DefaultGame.ini`:

```ini
[/Script/FactoryWorld3D.FWNetSubsystem]
ServerUrl="ws://localhost:5080/ws"
```

Adres musi być w cudzysłowie, bo w plikach `.ini` Unreala `//` zaczyna komentarz.

Serwer lokalnie: w repozytorium `factory-world` uruchom `docker compose up` albo bazę PostgreSQL (użytkownik i hasło `factory`, baza `factory_world`) i `dotnet run --project server/src/FactoryWorld.Server --launch-profile http`.

Po starcie gra pokazuje ekran logowania (e-mail i hasło konta z Factory World). Sesja jest zapisywana w `Saved/Auth/session.token`, więc przy następnym uruchomieniu gra loguje się sama.

Polecenia konsoli (klawisz `~`) do testów:

- `fw.Register <email> <hasło>`, `fw.Login <email> <hasło>`: logowanie bez ekranu logowania, wynik (`character.get`, `world.info`) trafia do logu
- `fw.WorldInfo`: pyta serwer o `world.info`

## Poziom testowy

`Content/Maps/TestLevel` to pusta mapa z realistycznym oświetleniem (`AFWEnvironment`: słońce, atmosfera, chmury wolumetryczne, mgła, Lumen) i kilkoma bryłami rzucającymi cienie. Można ją odtworzyć skryptem:

```
"C:\Program Files\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor-Cmd.exe" "%CD%\FactoryWorld3D.uproject" -run=pythonscript -script="%CD%\Tools\make_test_level.py"
```

## Gra na działce (demo)

Po zalogowaniu:

1. Konto bez postaci: gra pyta o nazwę i tworzy postać (`character.create`, wygląd domyślny).
2. Gracz bez działki: gra szuka najbliższej wolnej działki wokół `StarterPlotX/Y` (domyślnie Tatry, gdzie są kopalnie, a zakup daje kilof) i proponuje jej kupno (`world.buy`). Nowe konto ma 0 $, więc pieniądze trzeba najpierw zarobić w garażu w grze 2D.
3. `plot.enter`: działka powstaje w 3D. Teren z `tiles` to drzewa, skały, wydmy, krzaki, tory, stacja i kopalnie, a obiekty mają swoje rozmiary i obroty. Na razie to modele zastępcze, docelowe modele przyjdą w następnym kroku.

Sterowanie:

| Klawisz | Działanie |
| --- | --- |
| WASD, mysz | ruch i rozglądanie |
| Shift, Spacja | bieg, skok |
| E (przytrzymaj) | kopanie w kopalni (`plot.mine`, czas z `mineSeconds`); przy skrzyni wkłada do niej surowce (`storage.put`) |
| PPM (przytrzymaj) | zabiera obiekt do ekwipunku (`plot.pickup`) |
| B, 1–5 | tryb budowania i wybór przedmiotu z paska (`hotbar.get`); pusty pasek wypełnia się tym, co można postawić |
| LPM, R | stawia przedmiot (`plot.place`) na polu pod celownikiem, R obraca (0 południe, 1 wschód, 2 północ, 3 zachód) |

Taśmociągi i podajniki: gra co sekundę pyta o `logistics.get` (`compact: true`), a między odpowiedziami liczy ruch przedmiotów tymi samymi zasadami co serwer, więc przedmioty jadą płynnie. Podajniki potrzebują prądu (słup plus panel słoneczny lub wiatrak).

Ustawienia w `Config/DefaultGame.ini`, sekcja `[/Script/FactoryWorld3D.FWGameSettings]`:

- `TileSizeCm=400`: jak duże jest jedno pole działki. Pole na serwerze ma 20 m, co z perspektywy pierwszej osoby jest za duże, więc działka (250 × 250 pól) ma 1 km.
- `StarterPlotX`, `StarterPlotY`: gdzie szukać pierwszej działki.
- `LogisticsPollSeconds`: jak często pytać o taśmociągi.

Testy bez klawiatury: polecenia konsoli `FWPrompt`, `FWGoto`, `FWMine`, `FWPlace`, `FWPickupAt`, `FWStoreAt`, `FWBuildLine`, `FWStoreLine`, `FWViewBelts`, `FWBuildMode` i `FWShot`. Plik poleceń uruchamia się z `-FWAutoTest=<ścieżka>` (przykład: `Tools/autotest_plot.txt`).

Materiały bazowe (`Content/Materials`) tworzy skrypt `Tools/make_materials.py`.

![Tryb budowania](docs/screenshots/build_mode.png)

## Automatyczne logowanie (tylko do testów)

Żeby gra od razu wchodziła na działkę, bez ekranu logowania, utwórz plik `Saved/DevLogin.ini`:

```ini
Email=explore3d@example.test
Password=<hasło konta testowego>
```

Katalog `Saved/` nie trafia do gita, więc hasła nie ma w repozytorium. Plik jest pomijany w wersji Shipping. Żeby wyłączyć automatyczne logowanie, usuń plik albo zostaw puste `Email=`.

Konto bez działki i bez pieniędzy dostaje propozycję kupna działki i przycisk „Rozejrzyj się (bez działki)”, więc gra nie utyka.

## Grafika: zasoby zewnętrzne

Fotogrametryczne skały, drzewa, krzaki, tekstury gruntu (Poly Haven, CC0) i prawdziwe ukształtowanie terenu (Copernicus DEM) nie są w repozytorium, bo zajęłyby zbyt dużo miejsca w Git LFS. Tworzy je jedno polecenie:

```
powershell -ExecutionPolicy Bypass -File Tools\setup_assets.ps1
```

Skrypt pobiera pliki i sprawdza ich sumy kontrolne, przygotowuje je w Blenderze (odchudza skany do budżetów Nanite i buduje teren wokół działki) i importuje do `Content/External`. Wymaga Node.js, Blendera 5.2 i UE 5.8 (ścieżki można zmienić parametrami). Bez tych zasobów gra też działa, tylko z prostymi kształtami zastępczymi. Licencje i źródła są opisane w [ASSETS.md](ASSETS.md).

Obraz: fizyczne słońce (75 000 lx), ekspozycja z histogramu w zakresie EV 12,5–14,8, krzywa filmowa, delikatny bloom, winieta i ziarno, Lumen z HW RT (oświetlenie trafień z surface cache), wirtualne mapy cieni, Nanite, mgła wolumetryczna.

## Wersja do grania (paczka dla Windows)

```
powershell -ExecutionPolicy Bypass -File Tools\package.ps1                         (Shipping)
powershell -ExecutionPolicy Bypass -File Tools\package.ps1 -Configuration Development
```

Skrypt kompiluje grę, przygotowuje zasoby (cook) i pakuje wszystko do `D:\Factory-World\Builds\<konfiguracja>\Windows`. Najpierw trzeba uruchomić `Tools\setup_assets.ps1`, bo grafika z `Content/External` nie jest w gicie i trafia do paczki z tego komputera (`DirectoriesToAlwaysCook` w `Config/DefaultGame.ini`). Ostrzeżenia z przygotowania zasobów są zapisywane w `CookWarnings.txt` obok paczki.

- Adres serwera: `FactoryWorld3D.exe -ServerUrl=ws://adres:5080/ws` albo `ServerUrl` w `Saved\Config\Windows\Game.ini`.
- Wersja Shipping pomija `Saved/DevLogin.ini`, więc zawsze pokazuje ekran logowania. Wersja Development loguje się automatycznie, jeśli ten plik istnieje.

## Las (Project Titan)

Las świerkowo-sosnowy na zboczach wokół działki to drzewa Nanite z darmowego przykładu Epic **Project Titan** (Fab). Nie są w repozytorium (licencja pozwala używać ich w grze, ale nie rozpowszechniać osobno). Żeby je dodać, pobierz Titana w Epic Launcherze („Create Project”, UE 5.8) i uruchom:

```
powershell -ExecutionPolicy Bypass -File Tools\migrate_titan.ps1 -Titan D:\Titan\ProjectTitan
```

Las zaczyna się tuż za działką: gęsty do ok. 150 m nad działką, wyżej rzednie i przechodzi w pojedyncze sosny z porostami do ok. 300 m, ponad nimi są hale i skały. Działka i pas 40 m wokół niej zostają wolne pod budowę. Rośnie płatami z polanami, a pod drzewami grunt to ciemna ściółka z igieł. Z daleka las widać też jako ciemny kolor koron w materiale terenu.

Wysokości lasu ustawia `Config/DefaultGame.ini`: `ForestTopAbovePlotMeters` i `TreeLineAbovePlotMeters`. Po ustawieniu `bForestRelativeToPlot=False` las wraca do prawdziwej granicy lasu w Tatrach (`ForestTopMeters=1300`, `TreeLineMeters=1580` m n.p.m.). Wysokość działki n.p.m. zapisuje `Tools/dem_extract.mjs`, a import dołącza ją do siatki terenu (działka testowa: 2020 m).

Drzew jest najwyżej 28 000. Te dalej niż 1 km od działki nie rzucają cieni, a wiatr porusza tylko drzewa w promieniu 50 m. Drzewa do 400 m od działki mają kolizję: niewidoczny walec w miejscu pnia, dużo tańszy niż kolizja całego drzewa. Las nie bierze udziału w ray tracingu. Bez Titana gra działa normalnie, tylko bez drzew.

## Kosodrzewina

Nad lasem rośnie pas kosodrzewiny (Pinus mugo), jak w Tatrach. Zaczyna się na górnej granicy gęstego lasu i sięga 250 m wyżej (`DwarfPineBandMeters` w `Config/DefaultGame.ini`). Rośnie w zwartych płatach z przerwami między nimi. Niżej pojedyncze kępy zajmują polany w lesie, także tuż za pasem wolnym od budowy. Omija strome zbocza i skały.

Krzewy robimy sami w Blenderze (`Tools/blender/make_mugo_pine.py`, 5 odmian). Z jednej nasady wychodzi wiele wygiętych pędów, które łukiem rozchodzą się na boki. Gęste szczotki igieł to prawdziwa geometria (Nanite), a nie płaskie karty z przezroczystością. Kora ma teksturę z modelu `pine_tree_01` z Poly Haven (CC0), a igły kolor z materiału `M_FWFoliage`. Ten materiał porusza krzewami na wietrze, ale tylko w promieniu 15 m od gracza. Kępy do 400 m od działki są nie do przejścia, tak jak prawdziwa kosodrzewina: dostają niewidoczny walec kolizji.

Krzewy powstają razem z innymi zasobami (`Tools/setup_assets.ps1`) i nie ma ich w gicie. Bez nich gra działa normalnie, tylko bez kosodrzewiny.
