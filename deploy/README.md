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

### Wariant: Google Cloud (Compute Engine)

Zamiast punktów 1, 3 i 4 powyżej (punkt 2, domena, bez zmian):

1. W [console.cloud.google.com](https://console.cloud.google.com) utwórz projekt (np. `factoryworld3d`) i podłącz
   rozliczenia.
   - Od razu ustaw budżet z alertem: Billing → Budgets & alerts, np. 30 $ miesięcznie, powiadomienia na e-mail.
2. Compute Engine → VM instances → Create instance:
   - nazwa `factoryworld3d`, region **europe-central2 (Warszawa)**, typ maszyny **e2-medium** (2 vCPU, 4 GB);
   - dysk startowy: Ubuntu 24.04 LTS, 30 GB, „Balanced persistent disk”;
   - Firewall: zaznacz **Allow HTTP traffic** i **Allow HTTPS traffic**;
   - Security → Manage access → Add item (SSH keys): wklej cały publiczny klucz
     `~/.ssh/factoryworld3d_deploy.pub`. Nazwą użytkownika na serwerze będzie końcówka klucza:
     `factoryworld3d-deploy`.
3. Stały adres IP: VPC network → IP addresses → przy zewnętrznym adresie maszyny „Promote to static”. Bez tego IP
   zmienia się po restarcie maszyny, a domena przestaje działać.
4. Połącz się i zainstaluj Dockera. Na Google loguje się zwykły użytkownik, więc komendy idą przez `sudo`. Zaporę
   zapewnia Google (punkt 2), więc `ufw` jest niepotrzebny.

   ```
   ssh -i ~/.ssh/factoryworld3d_deploy factoryworld3d-deploy@IP_SERWERA
   curl -fsSL https://get.docker.com | sudo sh
   sudo usermod -aG docker $USER && exit
   ```

   Po ponownym zalogowaniu `docker` działa bez `sudo`. W punkcie 2 poniżej katalog zakłada się raz przez sudo:
   `sudo mkdir -p /opt/factoryworld3d && sudo chown $USER /opt/factoryworld3d`. W poleceniach `scp` i `ssh` z dalszych
   punktów zamiast `root@` jest `factoryworld3d-deploy@`.

Koszt w przybliżeniu: e2-medium ok. 25–30 $ miesięcznie, dysk ok. 3 $, ruch wychodzący ok. 0,12 $ za GB. Nowe konto
ma 300 $ kredytu na 90 dni.

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

## 8. Launcher: gra dla znajomych z automatycznymi aktualizacjami

Gracze pobierają raz launcher:

```
https://twoja-domena/download/FactoryWorld3D-Launcher.exe
```

Launcher instaluje grę w `%LOCALAPPDATA%\FactoryWorld3DGame` (bez uprawnień administratora). Przy każdym
uruchomieniu:
- sprawdza na serwerze `download/manifest.json`;
- pobiera tylko zmienione pliki (wznawia przerwane pobieranie i sprawdza sumy SHA-256);
- usuwa pliki, których nowa wersja już nie ma;
- przycisk „Graj” uruchamia grę z adresem serwera.

Nowszą wersję launchera wymienia sam. Opcje: `--play` uruchamia grę od razu po aktualizacji (np. na skrót na
pulpicie), a `--dir <folder>` instaluje grę w innym miejscu.

Wydanie nowej wersji gry (na komputerze z grą, gdy gra jest zamknięta):

```
powershell -ExecutionPolicy Bypass -File Tools\dev\publish-game.ps1 -Build
```

Skrypt:
1. pakuje grę (punkt 3, z szyfrowaniem z punktu 9) i buduje launcher;
2. liczy sumy plików;
3. wysyła kluczem SSH na serwer tylko pliki, których tam jeszcze nie ma (`deploy/download/files/<sha256>`);
4. na końcu jednym ruchem podmienia `manifest.json`.

Gracze nigdy nie widzą połowy wersji. Gotową paczkę można też wydać bez budowania:
`-PackageDir <folder z FactoryWorld3D.exe>`.

Caddy serwuje pod `/download/` wyłącznie katalog `deploy/download` na serwerze, tylko do odczytu. Są w nim tylko
manifest, launcher i pliki gry, bez listy plików, repozytorium ani `.env`. Stare pliki w `files/` można co jakiś czas
usunąć: wszystko, czego nie wymienia aktualny `manifest.json`.

## 9. Szyfrowanie paczki gry

Archiwa gry (modele, tekstury, dźwięki) są szyfrowane AES-256 i podpisane, więc zwykłe narzędzia ich nie otworzą.
Klucze leżą **poza repozytorium**: `D:\Factory-World\keys\FactoryWorld3D-Crypto.ini` (albo w pliku wskazanym przez
zmienną `FW3D_CRYPTO_INI`). `package.ps1` kopiuje go do `Config/DefaultCrypto.ini` tylko na czas pakowania i potem
usuwa. Ten plik jest też w `.gitignore`.

**Zrób kopię zapasową tego pliku**, np. na pendrive i w menedżerze haseł. Bez niego każda nowa paczka dostanie nowe
klucze, a launcher pobierze wtedy całą grę od nowa (zamiast kilku zmienionych plików). Kto ma ten plik, odszyfruje
grę, więc nie wysyłaj go nikomu.

Nowe klucze (tylko gdy stare wyciekły) generuje:

```
"C:\Program Files\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor-Cmd.exe" FactoryWorld3D.uproject -run=CryptoKeys -updateallkeys
```

Wynik trafia do `Config/DefaultCrypto.ini`. Trzeba w nim przestawić `bEncrypt…` i `bEnablePakSigning` na `True`,
a potem przenieść go do `D:\Factory-World\keys\`.

Szyfrowanie utrudnia, ale nie uniemożliwia wyciągnięcia plików: klucz musi być w grze, żeby mogła je czytać.
