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
