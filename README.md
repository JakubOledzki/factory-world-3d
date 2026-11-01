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

1. Konto bez postaci: otwiera się kreator postaci (opis niżej), a po kliknięciu „Utwórz postać” gra od razu przechodzi dalej.
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

## Kreator postaci

Po założeniu konta, gdy serwer odpowie `character.none`, otwiera się kreator. Ma te same opcje co edytor w grze 2D, w tej samej kolejności: skóra, twarz, oczy, fryzura, kolor włosów, zarost, koszulka, kolor koszulki, spodnie i buty, a do tego imię. Listy kolorów i liczbę stylów podaje serwer (`character.options`), więc nowy kolor dodany na serwerze pojawi się bez aktualizacji gry. Imię sprawdzamy według reguły serwera (3–16 liter, cyfr lub _), a postać tworzy `character.create`.

Obok jest podgląd 3D: manekin UE5 w ciemnym studiu z trzema światłami, na obracanym podeście. Przeciągnięcie myszą obraca postać, kółko przybliża twarz. Manekin jest malowany przez materiał `M_FWCharacter` (`Tools/character_material.hlsl`) według części ciała: kolory skóry, włosów, oczu i ubrań, fryzura, zarost, twarz i krój koszulki. Manekina nie ma w gicie, kopiuje go z szablonów silnika polecenie:

```
powershell -ExecutionPolicy Bypass -File Tools\copy_mannequins.ps1
```

Bez niego kreator działa, tylko podgląd jest pusty.

Fryzury, zarost i ubrania to prawdziwe modele 3D (`Tools/blender/make_character_gear.py`): po jednym na każdy styl, który podaje serwer (5 fryzur, 3 rodzaje zarostu, t-shirt, bezrękawnik, bluza z kapturem, spodnie, buty). Każdy jest wycięty z ciała manekina, odsunięty od skóry i pogrubiony, więc ma te same wagi kości co ciało i porusza się razem z nim. Kucyk i kaptur to dodatkowe kształty. Włosy i zarost to warstwy pasków („hair cards”) z pasmami wyciętymi przezroczystością, anizotropowym połyskiem i jaśniejszymi końcówkami, na ciemnej warstwie spodniej. Kolory wybrane w kreatorze nakładają materiały `M_FWHairCard` i `M_FWHair` i `M_FWCloth` (tkaniny CC0 z Poly Haven: dzianina, polar, dżins, skóra). Modele tworzy `Tools/setup_assets.ps1` (eksport manekina z edytora, Blender, import), gdy manekin jest już skopiowany. Bez nich podgląd pokazuje malowanego manekina.

## Plecak, skrzynie i menu

- **Tab / I** otwiera plecak: pola ułożone tak jak na serwerze, z ikonami i liczbą sztuk, a pod nimi pasek szybkiego wyboru. Przedmioty przeciąga się między polami (`inventory.move`) i na pasek (`hotbar.set`). Przeciągnięcie z paska do plecaka czyści pole paska.
- **E na skrzyni** otwiera plecak i obok zawartość skrzyni (`storage.get`). Przeciąganie w obie strony (`storage.put` / `storage.take`), Shift + klik przenosi cały stos, są też przyciski „Włóż wszystko” i „Weź wszystko”.
- **Esc** otwiera ustawienia: głośność, czułość myszy, pole widzenia i jakość grafiki (cztery poziomy). Zapisują się w `GameUserSettings.ini` na tym komputerze. Są też przyciski „Wróć do gry” i „Wyjdź z gry”.

Do testów: `FWInventory`, `FWChestNearest`, `FWMoveTile <z> <do>`, `FWStorage <1 włóż / 0 weź> <przedmiot> <ile>`, `FWMenu`, `FWClose`.

## Stół warsztatowy, piece i maszyny

**E** na stanowisku pracy otwiera jego okno. Jest odświeżane z serwera co 1,5 s i po każdej akcji.

- **Stół warsztatowy** (też kowadło, stocznia, krawiec): wszystkie przepisy z tym, czego potrzebują (zielone: masz, czerwone: brakuje), czasem i informacją, czy trzeba coś zbadać. Do tego przycisk „Wytwórz” i kolejka z postępem i anulowaniem (`crafting.get/craft/cancel`). Gotowe rzeczy trafiają do plecaka.
- **Piece** (kamienny, ceglany, hutniczy): ruda, węgiel i sztabki z paskiem wytopu, przyciski „Włóż rudę”, „Dołóż węgla”, „Weź sztabki” i lista tego, co piec wytapia (`furnace.get/load/take`).
- **Maszyny przetwórcze** (71 rodzajów jak w grze 2D: tartaki, młyny, kamieniołomy, montownie…): wybór przepisu, wsad na partię z przyciskiem „Włóż”, opał dla maszyn z paleniskiem, postęp, prędkość przy braku prądu, gotowy produkt i serwis zużytej maszyny (`machine.get/recipe/load/take/service`).

Do testów: `FWPlaceNear <rodzaj>`, `FWOpenNearest <rodzaj>`, `FWStationDo <typ> <pole> <wartość>`, `FWStationLoad <typ> <przedmiot> <ile>`.

## Rynek

Jak w grze 2D, rynek otwiera się klawiszem **E przy komputerze** na działce:

- **Handlarz:** ceny kupna i sprzedaży u handlarza NPC ze zmianą względem zwykłej ceny i bieżącym wydarzeniem rynkowym. Kupujesz 1 albo 10 sztuk, sprzedajesz 1 albo wszystko (`market.prices/buy/sell`).
- **Giełda graczy:** lista przedmiotów z najniższą ceną, a po wybraniu przedmiotu oferty sprzedaży i kupna innych graczy. Możesz kupić z oferty, sprzedać do zapytania albo wystawić własną ofertę: ile sztuk, po ile, sprzedaż albo kupno (`market.overview/item/purchase/fill/list/order`).
- **Moje oferty:** twoje oferty z postępem i przyciskiem „Wycofaj” (`market.mine/unlist/cancelOrder`).

U góry jest wyszukiwarka i stan konta.

Do testów: `FWMarket`, `FWMarketTab <0–2>`, `FWMarketItem <przedmiot>`, `FWTrade <1 kup / 0 sprzedaj> <przedmiot> <ile>`, `FWMarketOffer <1 sprzedaż / 0 kupno> <ile> <cena $>`.

## Mapa świata

Klawisz **N** (jak w grze 2D) otwiera mapę świata: cała Ziemia w działkach 5 × 5 km, kolory terenu i granice państw, nazwy miast (im bliżej, tym mniejsze miasta). Przeciągnij, aby przesunąć, kółkiem przybliżasz; mapa zawija się ze wschodu na zachód. Twoje działki są złote, działki innych graczy w widoku czerwone (`world.info`, `world.area`).

Kliknięcie wybiera działkę: położenie, teren, państwo, właściciel i cena (`world.plot`). Wolną działkę można kupić (`world.buy`), na swoją albo publiczną wejść (`plot.enter`), a swoją nazwać (`world.rename`).

Dane mapy pochodzą z plików gry 2D: `node Tools/build_world_map.mjs` pobiera `biomes.bin.gz`, `countries.bin.gz` i `towns.tsv.gz`, kopiuje je do `Content/External/WorldData` i rysuje `RawAssets/processed/world_map.png`, który `Tools/import_external.py` importuje jako `/Game/External/WorldMap`. Bez tych plików mapa pokazuje same działki.

Do testów: `FWMap`, `FWMapCenter <x> <y> <pikseli na działkę>`, `FWMapSelect <x> <y>`, `FWGoPlot <x> <y>`.

## Ręce i cień gracza

W widoku z pierwszej osoby widać ręce postaci w jej ubraniu (rękawy koszulki, bluzy albo gołe ramiona przy bezrękawniku) i w jej kolorze skóry. Ręce trzyma przed kamerą dwukośćowe IK na manekinie, bez gotowych animacji:

- lekko oddychają,
- kołyszą się w rytm kroków,
- przy obrocie głowy chwilę zostają w tyle,
- w trybie budowania są wyżej.

Ruchy: postawienie (pchnięcie obiema rękami), obrót (skręt prawej dłoni), zabranie (sięgnięcie i przyciągnięcie), kopanie (zamach i uderzenie przy każdym uderzeniu kilofa). Ręce mają własne pole widzenia (70°) i są przyciągnięte do oka, więc nie wchodzą w ściany. Resztę ciała wycinają materiały `M_FWCharacterArms` i `M_FWClothArms`.

Cień rzuca osobna, niewidoczna dla gracza pełna postać w tym samym wyglądzie. Stoi, chodzi, biega albo spada (animacje manekina z szablonów silnika, kopiuje je `Tools/copy_mannequins.ps1`). Wygląd przychodzi z serwera po zalogowaniu (`character.get` → `appearance` + palety z `character.options`).

**Kilof:** przy kopaniu prawa ręka zaciska się na kilofie (model z Blendera, `Tools/blender/make_tools.py`: kuta głowica z kilofem i dłutem, trzonek z jesionu z gumowym owijem; tekstury CC0 z Poly Haven). Przy każdym uderzeniu: dźwięk, lekkie szarpnięcie widoku, odpryski skały (fizyka, znikają po 2 s) i ślad na kamieniu (znika po kilkunastu sekundach). Bez kopania dłonie są lekko zgięte.

**Pasek i przedmiot w dłoni:** wybrany przedmiot z paska (1–5 albo kółko myszy) widać w rękach: mały model tego, co się buduje, trzymany między dłońmi i powoli obracany, a kilof w prawej dłoni. Przy zmianie przedmiotu ręce chowają się i wracają z nowym. Pasek ma kwadratowe pola z ikonami wyrenderowanymi z tych samych modeli (`Tools/blender/render_icons.py`, Cycles, przezroczyste tło), liczbą sztuk i podświetlonym, większym wybranym polem, a nad nim nazwę wybranego przedmiotu.

Do testów: `FWSelect <0–4>`, `FWGesture place|rotate|pickup|swing|mine`, `FWDress <koszulka 0–2> <kolor hex>`, `FWWalk <s>`.

## Czat, osiągnięcia i rywalizacja

Jak w grze 2D:

- **Czat** w lewym dolnym rogu: ostatnie linie same znikają po kilkunastu sekundach. **Enter** otwiera historię i pole do pisania: do graczy na działce albo do całego świata, z szybkimi zwrotami (Cześć!, Dzięki!...). Esc albo pusty Enter zamyka. Ogłoszenia serwera (meteoryt, pierwsi na świecie) są na złoto (`chat.get` co 2 s, `chat.send`).
- **Osiągnięcia** (O): odznaki w grupach z postępem, rankingi najlepszych graczy z własnym miejscem, rywalizacja: wyzwanie tygodnia i zwycięzcy, meteoryt z kopaniem, cuda świata, pierwsi na świecie (`achievements.get`, `rankings.get`, `rivalry.get`, `meteor.get/dig`, `company.wonders`).
- **Nowa odznaka**: karta wysuwa się z góry ekranu, gdy serwer zgłosi zdobycie (`achievements.watch` → `achievement.unlocked`); znaleziska do albumu jako komunikat (`album.found`).

Do testów: `FWChatOpen`, `FWSay <plot/world> <tekst>`, `FWAchievements <0–2>`, `FWRanking <id>`, `FWBadgeTest <nazwa>`.

## Dźwięk

Wszystkie dźwięki (49) są syntezowane przez nasz skrypt `Tools/make_audio.py` (Python z numpy, uruchamiany w Blenderze), bez nagrań i bez licencji do pilnowania. Tworzy je `Tools/setup_assets.ps1` i nie ma ich w gicie.

- **Otoczenie:** wiatr halny w stereo (szum i świst, które narastają w podmuchach), co jakiś czas osobny podmuch, szum liści w lesie i ptaki (4 rodzaje śpiewu). Wiatr jest głośniejszy wyżej i na otwartym terenie, a cichszy w lesie. Szum liści i ptaki są tam, gdzie las (ta sama reguła, według której sadzone są drzewa).
- **Kroki** zależą od podłoża: trawa, skała (strome zbocza, głazy) albo metal (wszystko, co zbudowane). Do tego skok i lądowanie.
- **Fabryka:** taśmociągi (szum, silnik, stukot rolek), serwo podajnika przy każdym ruchu ramienia i stuk chwytaka, skrzynia (otwarcie i zamknięcie), buczenie linii energetycznych, szum łopat turbin. Słychać tylko kilka najbliższych maszyn każdego rodzaju: 6 taśm, 3 słupy, 3 turbiny. Dlatego 100 taśm kosztuje tyle co 6.
- **Budowanie i interfejs:** postawienie, obrót, zabranie, kopanie (uderzenia kilofa), „nie można tu postawić”, kliknięcia i najechanie na przyciski, dźwięk utworzenia postaci.
- **Miks:** klasy dźwięku Master → Ambience / SFX / UI, wygaszanie z odległością i limity głosów. Głośność ogólną ustawia `MasterVolume` w `Config/DefaultGame.ini` (menu ustawień jeszcze nie ma).

Do testów: `FWRecordStart <s>` i `FWRecordStop <nazwa>` nagrywają to, co gra gra, do `Saved/BouncedWavFiles`, a `FWWalk <s>` idzie prosto. Gra w tle jest wyciszona (domyślnie w Unrealu), więc do nagrań bez fokusu uruchom ją z `-ini:Engine:[Audio]:UnfocusedVolumeMultiplier=1.0`.

## Badania, zlecenia i statystyki

Jak w grze 2D:

- **Stół badawczy** (E przy stole `furniture_research_table`): drzewko technologii osobno dla każdej gałęzi (zakładki u góry, z postępem). Karty stoją w kolumnach etapów, połączone krzywymi z tym, czego wymagają; wymagania z innych gałęzi to małe ikony na karcie. Po prawej opis, co odblokowuje, koszt (wymagane/masz), czas i XP, przyciski **Zbadaj** / **Przerwij badanie** i przyspieszanie danymi badawczymi (`research.get/start/cancel/boost`). Badania spółki: dokładanie przedmiotów i pieniędzy (`research.companyStart/contribute/companyCancel`).
- **Zlecenia** (J): zlecenia firm i przyjęte zlecenia graczy z dostarczaniem z ekwipunku i rezygnacją, tablica zleceń innych graczy, własne zlecenia z formularzem i odwoływaniem (`contracts.get/deliver/abandon/accept/create/cancel`).
- **Statystyki produkcji** (L): okres 10 min / 1 h / 24 h, wąskie gardła (maszyny stojące i czego im brak, kończące się zapasy), praca maszyn, wykres produkcji i zużycia wybranego przedmiotu oraz tabela na minutę (`stats.production`, co 5 s).

Do testów: `FWResearch`, `FWResearchPick <id>`, `FWResearchStart`, `FWContracts <0–2>`, `FWContractFirst <deliver/accept/abandon/cancel> <npc/taken/board/issued> [ile]`, `FWContractCreate <przedmiot> <ile> <nagroda $> <godziny>`, `FWStats <minuty> [przedmiot]`.

## Automatyczne logowanie (tylko do testów)

Żeby gra od razu wchodziła na działkę, bez ekranu logowania, utwórz plik `Saved/DevLogin.ini`:

```ini
Email=explore3d@example.test
Password=<hasło konta testowego>
```

Katalog `Saved/` nie trafia do gita, więc hasła nie ma w repozytorium. Plik jest pomijany w wersji Shipping. Żeby wyłączyć automatyczne logowanie, usuń plik albo zostaw puste `Email=`.

Konto bez działki i bez pieniędzy dostaje propozycję kupna działki i przycisk „Rozejrzyj się (bez działki)”, więc gra nie utyka.

## Transport: stacja, kursy, trasy i eksport

Jak w grze 2D:

- **Stacja kolejowa** (T): **Trasy** między własnymi działkami (ze skrzyń ładujących do odbierających, nazwa działki), **Magazyn** stacji (ładowanie z ekwipunku i zdejmowanie), **Wyślij** jeden pociąg – albo statek z portu, ciężarówkę z bazy, sanie, wóz konny – na swoją działkę, do innego gracza (szukanie po nazwie) lub na podane X, Y, z wyliczeniem odległości, czasu i opłaty, opcjonalnie jako przemyt; **Rozkład** – kursy odjeżdżające same co kilka minut; **W drodze** – pociągi przyjeżdżające i odjechane z odliczaniem (`station.*`, `freight.*`).
- **Skrzynia eksportowa** (E przy niej): ceny towarów, wkładanie i wyjmowanie, droga do najbliższego rynku skupu i pociągi w drodze (`export.get/load/unload/price`).
- **Rynek skupu i port pociągowy** (E przy nich): prowizja rynku albo opłata portu, ustawiana przez właściciela, i zarobek (`export.post/setPost`).

Do testów: `FWRail <0–4>`, `FWRailStock <1 załaduj / 0 zdejmij> <przedmiot> <ile>`, `FWExport [id rynku lub portu]`.

## Grafika: zasoby zewnętrzne

Fotogrametryczne skały, drzewa, krzaki, tekstury gruntu (Poly Haven, CC0) i prawdziwe ukształtowanie terenu (Copernicus DEM) nie są w repozytorium, bo zajęłyby zbyt dużo miejsca w Git LFS. Tworzy je jedno polecenie:

```
powershell -ExecutionPolicy Bypass -File Tools\setup_assets.ps1
```

Skrypt pobiera pliki i sprawdza ich sumy kontrolne, przygotowuje je w Blenderze (odchudza skany do budżetów Nanite i buduje teren wokół działki) i importuje do `Content/External`. Wymaga Node.js, Blendera 5.2 i UE 5.8 (ścieżki można zmienić parametrami). Bez tych zasobów gra też działa, tylko z prostymi kształtami zastępczymi. Licencje i źródła są opisane w [ASSETS.md](ASSETS.md).

Obraz: fizyczne słońce (75 000 lx), ekspozycja z histogramu w zakresie EV 12,5–14,8, krzywa filmowa, delikatny bloom, winieta i ziarno, Lumen z HW RT (oświetlenie trafień z surface cache), wirtualne mapy cieni, Nanite, mgła wolumetryczna.

## Spółka, banki i giełda

Jak w grze 2D:

- **Spółka** (K): bez spółki zakładanie i zaproszenia; w spółce zakładki **Spółka** (członkowie i role, zapraszanie, działki spółki z kierownikami, wnoszenie własnych działek, wspólny magazyn – wkładanie i wyjmowanie, dziennik), **Kasa** (wpłata za udziały, wypłata, sprzedaż z magazynu handlarzowi, zakup do magazynu, udziały, dywidenda, trofea), **Projekty** (wielkie budowy i cuda świata, dokładanie części z ekwipunku i magazynu), **Zlecenia** (zlecenia tygodnia, zadania z pensją), **Umowy** (stałe dostawy z innymi spółkami), **Giełda** (akcje, wejście na giełdę, przejęcie) i **Ranking** sezonu (`company.*`).
- **Banki** (Z – w 3D B to budowanie): konta, wpłata, wypłata, przelewy do innych graczy, pieniądze „w drodze” z odliczaniem (`bank.*`, odświeżanie co 5 s).

Do testów: `FWCompany <0–6>`, `FWCompanyDo <wiadomość> <pole> <wartość>` (np. `FWCompanyDo company.deposit cents 50000`), `FWBank`.

## Wersja do grania (paczka dla Windows)

```
powershell -ExecutionPolicy Bypass -File Tools\package.ps1                         (Shipping)
powershell -ExecutionPolicy Bypass -File Tools\package.ps1 -Configuration Development
```

Skrypt kompiluje grę, przygotowuje zasoby (cook) i pakuje wszystko do `D:\Factory-World\Builds\<konfiguracja>\Windows`. Najpierw trzeba uruchomić `Tools\setup_assets.ps1`, bo grafika z `Content/External` nie jest w gicie i trafia do paczki z tego komputera (`DirectoriesToAlwaysCook` w `Config/DefaultGame.ini`). Ostrzeżenia z przygotowania zasobów są zapisywane w `CookWarnings.txt` obok paczki.

- Adres serwera: `FactoryWorld3D.exe -ServerUrl=ws://adres:5080/ws` albo `ServerUrl` w `Saved\Config\Windows\Game.ini`.
- Wersja Shipping pomija `Saved/DevLogin.ini`, więc zawsze pokazuje ekran logowania. Wersja Development loguje się automatycznie, jeśli ten plik istnieje.

## Miasto, państwo i policja

Jak w grze 2D:

- **Miasto** (H – ratusz): potrzeby miasta z dostawą z ekwipunku lub ze stacji, tygodniowy przetarg (ukryte oferty z wadium, dostawa zwycięzcy), rozwój miasta i główny dostawca, wywóz śmieci, usługi biur graczy, ratusz (podatki i premia burmistrza, festyn, uchwały z głosowaniem, wotum nieufności), wybory burmistrza (głosowanie, kandydowanie z programem i kaucją, wsparcie spółek) i kasa miasta z budowami (`city.*`). Na działce w powiecie, poza samym miastem, tylko ratusz, wybory, kasa i przetarg.
- **Państwo** (P): obywatelstwo, prezydent i ministrowie, prawo i skarbiec, rząd (podatki, opłaty, cła, dotacje, inwestycje, mianowanie ministrów), wybory prezydenta z wotum nieufności, dyplomacja (sojusze, embarga), ranking państw i partie (`country.*`, `party.*`).
- **Policja** (przycisk w oknie państwa): prawo i licencje, sprawy i kary, zgłaszanie graczy, służba (kontrole pociągów, wyroki, śledztwa, nakazy, funkcjonariusze), sąd (odwołania, nakazy) i paser (`law.*`, `fence.*`).

Do testów: `FWCity <0–7>`, `FWCountry <0–5> [kod kraju, np. SVK]`, `FWPolice <0–3>`.

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

## Modele stołów, pieców i maszyn

`Tools/blender/make_factory.py` buduje osobne modele w prawdziwej skali dla zajmowanego obszaru (1 pole = 4 m): stół warsztatowy (imadło, tablica z narzędziami), kowadło na pniu, komputer z dwoma monitorami i krzesłem, stół badawczy (mikroskop, kolby), kamienny piec z kominem, ceglany piec z kolebkowym sklepieniem, wielki piec z nagrzewnicami i pochylnią, tartak z piłą tarczową pod wiatą, montownię z ramieniem robota i ogrodzeniem oraz młyn wodny. Podobne rodzaje dzielą model, przeskalowany do swojego obszaru (`FWArt::FurnitureModel`); reszta maszyn ma dalej ogólną obudowę. Nowe tekstury CC0 z Poly Haven: `red_brick_03`, `castle_wall_slates`, `wood_table_001` (`node Tools/fetch_polyhaven.mjs`). Ikony na pasku renderuje `Tools/blender/render_icons.py`.

Do testów: `FWLookAt <rodzaj> <pola> <kąt> [pochylenie]` staje w danej odległości od najbliższego obiektu i patrzy na niego.
