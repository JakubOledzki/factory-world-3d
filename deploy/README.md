# Serwer gry 3D w internecie

Jak postawić serwer gry na zwykłym serwerze VPS i jak go potem utrzymywać. Na serwerze działają trzy kontenery
(`deploy/docker-compose.yml`):

- `db`: baza PostgreSQL 17, dostępna tylko dla serwera gry,
- `server`: serwer gry (`Server/`) w trybie Production: bez komend deweloperskich i bez botów,
- `caddy`: HTTPS przed serwerem. Sam pobiera i odnawia certyfikat Let's Encrypt, więc gra łączy się szyfrowanym
  `wss://`.

Hasła są tylko w pliku `deploy/.env` na serwerze. Ten plik nie trafia do gita.

## 1. Serwer i domena (raz)

1. Załóż serwer u dostawcy. Proponowany: Hetzner Cloud, CX22 (2 vCPU, 4 GB RAM), system Ubuntu 24.04.
   - Przy zakładaniu dodaj klucz SSH: publiczny klucz `~/.ssh/factoryworld3d_deploy.pub` z komputera, na którym jest
     gra.
   - Włącz kopie zapasowe dostawcy (Backups), jeśli są w ofercie: to kopia całego dysku.
2. W panelu domeny dodaj rekord `A` z nazwą (np. `gra.twojadomena.pl`) wskazującą na IP serwera.
   Domena jest potrzebna do certyfikatu HTTPS.
3. Połącz się z serwerem:

   ```
   ssh -i ~/.ssh/factoryworld3d_deploy root@IP_SERWERA
   ```

4. Na serwerze zainstaluj Dockera i włącz zaporę:

   ```
   curl -fsSL https://get.docker.com | sh
   ufw allow OpenSSH && ufw allow 80 && ufw allow 443 && ufw --force enable
   ```

## 2. Pierwsze uruchomienie

```
GIT_LFS_SKIP_SMUDGE=1 git clone https://github.com/JakubOledzki/factory-world-3d.git /opt/factoryworld3d
cd /opt/factoryworld3d/deploy
cp .env.example .env
nano .env        # DOMAIN=twoja domena, POSTGRES_PASSWORD= wynik: openssl rand -hex 24
docker compose up -d --build
```

Sprawdzenie (po minucie, gdy Caddy pobierze certyfikat): `https://twoja-domena/health` pokazuje `Healthy`.

Repozytorium jest pobierane bez plików Git LFS (`GIT_LFS_SKIP_SMUDGE=1`): serwer gry nie potrzebuje grafik ani modeli,
więc klonuje się szybko.

## 3. Gra dla graczy

Na komputerze z grą (Windows) zbuduj paczkę z adresem serwera:

```
powershell -ExecutionPolicy Bypass -File Tools\dev\package.ps1 -ServerUrl wss://twoja-domena/ws
```

Gotowa gra jest w `Saved\Package\Windows`: tę paczkę (spakowaną do .zip) dostają gracze. Adres serwera można też
podać przy uruchamianiu gry: `FactoryWorld3D.exe -ServerUrl=wss://twoja-domena/ws`.

## 4. Przeniesienie obecnej bazy z komputera (opcjonalnie)

Gdy na serwerze ma być świat z komputera (konta, postacie, działki), a nie pusty. Najlepiej przed pierwszym
logowaniem graczy na serwerze.

Na komputerze (gra i lokalny serwer zamknięte):

```
D:\Programs\PostgreSQL\17\bin\pg_dump.exe -h localhost -p 5433 -U factory -Fc -f factoryworld3d.dump factoryworld3d
scp -i ~/.ssh/factoryworld3d_deploy factoryworld3d.dump root@IP_SERWERA:/opt/factoryworld3d/deploy/
```

Na serwerze:

```
cd /opt/factoryworld3d/deploy
docker compose stop server
docker compose exec -T db pg_restore -U factory -d factoryworld3d --clean --if-exists --no-owner < factoryworld3d.dump
docker compose start server
rm factoryworld3d.dump
```

Uwaga: konta testowe (`@example.test`) i konta z listy `Dev:Accounts` przechodzą razem z bazą. Na serwerze
Production nie mają żadnych dodatkowych uprawnień.

## 5. Kopie bazy

Ręcznie: `./backup.sh` w `deploy/` zapisuje kopię do `deploy/backups/` i usuwa kopie starsze niż 14 dni.

Codziennie o 4:00 (raz, na serwerze):

```
chmod +x /opt/factoryworld3d/deploy/backup.sh
(crontab -l 2>/dev/null; echo "0 4 * * * /opt/factoryworld3d/deploy/backup.sh >> /var/log/factoryworld3d-backup.log 2>&1") | crontab -
```

Kopie na tym samym dysku nie chronią przed awarią serwera. Co jakiś czas pobierz je na komputer:

```
scp -i ~/.ssh/factoryworld3d_deploy "root@IP_SERWERA:/opt/factoryworld3d/deploy/backups/*.dump" D:\Factory-World\backups\
```

Przywrócenie kopii: jak w punkcie 4, z wybranym plikiem z `backups/`.

## 6. Aktualizacja serwera

Po scaleniu zmian do `main`:

```
cd /opt/factoryworld3d
./deploy/backup.sh
git pull
cd deploy && docker compose up -d --build server
```

Migracje bazy wykonują się same przy starcie serwera. Gracze są rozłączani na kilkanaście sekund.

Gdy zmienił się protokół gry (nowe wiadomości klienta), zbuduj też nową paczkę gry (punkt 3).

## 7. Logi i stan

```
cd /opt/factoryworld3d/deploy
docker compose ps                 # co działa
docker compose logs -f server     # logi serwera gry (Ctrl+C kończy)
docker compose logs caddy         # certyfikat, połączenia HTTPS
docker stats                      # pamięć i procesor
```

Logi serwera są ograniczone do 5 plików po 20 MB.
