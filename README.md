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
