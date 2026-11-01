# Serwer Factory World 3D

Własny serwer gry 3D: osobna baza danych, osobne konta i osobny świat. Nie ma nic wspólnego z serwerem gry 2D. Kod powstał jako kopia serwera z repozytorium `factory-world` (katalog `server/`) i od tej pory rozwija się tutaj niezależnie.

Serwer to ASP.NET Core (.NET 10) z bazą PostgreSQL. Klient łączy się przez WebSocket pod `ws://localhost:5081/ws` (`Config/DefaultGame.ini`). Schemat bazy tworzą migracje przy starcie, każdy moduł ma swoje.

## Uruchomienie lokalne

Potrzebne są .NET SDK 10 i PostgreSQL 17 (lokalnie albo w Dockerze).

### Z Dockerem

```
cd Server
docker compose up
```

Uruchamia bazę (port 5433) i serwer (port 5081).

### Bez Dockera

1. W lokalnym PostgreSQL utwórz użytkownika `factory` z hasłem `factory` i prawem tworzenia baz. Bazę `factoryworld3d` serwer utworzy sam przy pierwszym starcie.

   ```
   CREATE ROLE factory LOGIN PASSWORD 'factory' CREATEDB;
   ```

2. Uruchom serwer:

   ```
   dotnet run --project Server/src/FactoryWorld.Server --launch-profile http
   ```

Połączenie z bazą jest w `src/FactoryWorld.Server/appsettings.Development.json` (`Host=localhost;Port=5432;Database=factoryworld3d`). Inne ustawienie podaje się zmienną środowiskową `ConnectionStrings__Game`.

To hasło (`factory`) służy tylko do lokalnych testów. Na serwerze publicznym hasło do bazy podaje się zmienną środowiskową, nigdy w repozytorium. Wdrożenie gdziekolwiek poza własny komputer wymaga osobnej decyzji.

## Testy

```
dotnet test Server/FactoryWorld.slnx
```

## Produkcja tylko przy graczach

Działka produkuje tylko wtedy, gdy jej właściciel jest w grze. Działka dodana do spółki produkuje, gdy w grze jest dowolny członek spółki. Krótkie rozłączenie nic nie zmienia: działka pracuje jeszcze 90 s po wyjściu ostatniego gracza (`PlotClockRules.Grace`).

Każda działka ma swój zegar (`IPlotClock`): czas rzeczywisty minus wszystkie przerwy. Co 10 s `PlotClockKeeper` sprawdza, kto jest w grze, i zaczyna albo kończy przerwę działki (tabele `world.plot_pauses` i `world.plot_clock`). Po restarcie serwera działki stoją od ostatniego sprawdzenia przed wyłączeniem, aż wrócą ich gracze. Na zegarze działki chodzą taśmy, chwytaki, piece, maszyny, stołówki, prąd z elektrowni na działce, wiertła, szyby naftowe, pola, lasy, zwierzęta, karmniki i ule. Czasu rzeczywistego trzymają się pogoda, pociągi, handel, podatki, kontrakty, odrastanie zebranych zasobów oraz warsztat i maszyny w garażu (garaż nie jest działką).
