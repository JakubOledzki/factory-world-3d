# Zasoby zewnętrzne

Gra używa wyłącznie zasobów, które wolno wykorzystać w grze komercyjnej. Plików nie ma w repozytorium: skrypt `Tools/setup_assets.ps1` pobiera je, przygotowuje i importuje do `Content/External` (ten katalog i `RawAssets/` są w `.gitignore`). Bez nich gra działa, tylko z prostymi modelami zastępczymi.

## Poly Haven, licencja CC0

[CC0 1.0](https://creativecommons.org/publicdomain/zero/1.0/): domena publiczna, użycie komercyjne dozwolone, bez obowiązku podawania autora. Źródło: https://polyhaven.com (lista plików przez API `https://api.polyhaven.com/files/<id>`; skrypt sprawdza ich sumy kontrolne).

### Modele (glTF, tekstury 2K)

| Zasób | Adres | Użycie |
| --- | --- | --- |
| boulder_01 | https://polyhaven.com/a/boulder_01 | skały na działce i wokół niej |
| namaqualand_boulder_02 | https://polyhaven.com/a/namaqualand_boulder_02 | skały, kopalnie |
| namaqualand_boulder_04 | https://polyhaven.com/a/namaqualand_boulder_04 | skały, kopalnie |
| rock_09 | https://polyhaven.com/a/rock_09 | skały |
| rock_moss_set_01 | https://polyhaven.com/a/rock_moss_set_01 | skały |
| fir_tree_01 | https://polyhaven.com/a/fir_tree_01 | drzewa (jodły) |
| pine_tree_01 | https://polyhaven.com/a/pine_tree_01 | drzewa (sosny) |
| fir_sapling_medium | https://polyhaven.com/a/fir_sapling_medium | młode drzewka |
| shrub_01, shrub_02 | https://polyhaven.com/a/shrub_01, https://polyhaven.com/a/shrub_02 | krzaki |
| fern_02 | https://polyhaven.com/a/fern_02 | paprocie |
| grass_medium_01 | https://polyhaven.com/a/grass_medium_01 | trawa |

### Tekstury (PBR: kolor, normalne, AO/szorstkość/metaliczność)

| Zasób | Adres | Rozdzielczość | Użycie |
| --- | --- | --- | --- |
| rocky_terrain_02 | https://polyhaven.com/a/rocky_terrain_02 | 4K | grunt w górach |
| aerial_grass_rock | https://polyhaven.com/a/aerial_grass_rock | 4K | trawa ze skałami |
| rock_face_03 | https://polyhaven.com/a/rock_face_03 | 4K | strome zbocza |
| gravel_ground_01 | https://polyhaven.com/a/gravel_ground_01 | 2K | żwir (lodowiec) |
| snow_02 | https://polyhaven.com/a/snow_02 | 2K | śnieg na szczytach |
| forest_ground_04 | https://polyhaven.com/a/forest_ground_04 | 2K | grunt w lesie |
| coast_sand_01 | https://polyhaven.com/a/coast_sand_01 | 2K | piasek (pustynia) |
| dirt | https://polyhaven.com/a/dirt | 2K | ziemia |

### Tekstury fabryki (CC0, 2K)

Tekstury z Poly Haven nakładane na modele z `Tools/blender/make_factory.py` według nazw slotów materiałów:

| Zasób | Adres | Slot |
| --- | --- | --- |
| blue_metal_plate | https://polyhaven.com/a/blue_metal_plate | FW_PaintedSteel |
| metal_plate | https://polyhaven.com/a/metal_plate | FW_Steel |
| rusty_painted_metal | https://polyhaven.com/a/rusty_painted_metal | FW_Worn |
| rubberized_track | https://polyhaven.com/a/rubberized_track | FW_Rubber (przyciemniona do czarnej gumy) |
| weathered_planks | https://polyhaven.com/a/weathered_planks | FW_Wood |
| concrete_floor_worn_001 | https://polyhaven.com/a/concrete_floor_worn_001 | FW_Concrete |
| metal_grate_rusty | https://polyhaven.com/a/metal_grate_rusty | FW_Grate |

Same modele (taśmociągi, podajnik, skrzynia, słup, panel, wiatrak, tory, peron, maszyna, rudy, sztabki) są nasze: powstają ze skryptu.

## ambientCG, licencja CC0

Naklejki (decale) z zaciekami i plamami: https://ambientcg.com, licencja [CC0 1.0](https://docs.ambientcg.com/license/), użycie komercyjne dozwolone.

| Zasób | Adres | Użycie |
| --- | --- | --- |
| Leaking007 | https://ambientcg.com/view?id=Leaking007 | plamy oleju, zacieki |
| Leaking008 | https://ambientcg.com/view?id=Leaking008 | plamy oleju, zacieki |
| Leaking009 | https://ambientcg.com/view?id=Leaking009 | plamy oleju, zacieki |

## Copernicus DEM GLO-30

Numeryczny model terenu (30 m), z którego powstaje prawdziwe ukształtowanie Tatr wokół działki. Kafle `N49_00_E019_00` i `N49_00_E020_00` są pobierane z https://copernicus-dem-30m.s3.amazonaws.com.

Licencja: dane Copernicus DEM GLO-30 są bezpłatne do każdego celu, także komercyjnego, pod warunkiem podania źródła. Wymagana informacja (do napisów końcowych gry):

> © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.

Opis danych i licencji: https://registry.opendata.aws/copernicus-dem/

## Kosodrzewina (nasza)

Krzewy kosodrzewiny tworzy nasz skrypt `Tools/blender/make_mugo_pine.py`: geometria jest w całości nasza. Kora używa tekstur z `pine_tree_01` (Poly Haven, CC0, w tabeli wyżej). Igły nie mają tekstury, tylko kolor z materiału.

## Project Titan (Epic Games, Fab)

Sosny (`SM_Pine_A`, `SM_Pine_B`, wersje z porostami i sadzonki) z darmowego projektu przykładowego Epic Games **Project Titan** (Fab), razem z ich materiałami, teksturami i wtyczką `EasyImpostor`. Licencja: treści Epic z Fab dla projektów Unreal Engine, na warunkach Unreal Engine EULA i licencji Fab. Wolno ich używać w grze zbudowanej w Unreal Engine, w tym komercyjnej, ale **nie wolno ich rozpowszechniać osobno**, na przykład w publicznym repozytorium.

Dlatego nie ma ich w gicie (`.gitignore`: `Content/Environment/`, `Content/Landscape/`, `Content/Blueprint/FoliageInteraction/`, `Plugins/EasyImpostor/`). Każdy kopiuje je u siebie z własnej kopii Titana:

```
powershell -ExecutionPolicy Bypass -File Tools\migrate_titan.ps1 -Titan D:\Titan\ProjectTitan
```

Skrypt sprawdza zależności w Titanie (`Tools/titan_dependencies.py`) i kopiuje tylko potrzebne pakiety (110), z zachowaniem ich ścieżek, bo odwołania w Unrealu opierają się na ścieżkach. Bez nich gra działa, tylko bez lasu.

## Narzędzia

- Blender 5.2 (GPL) służy tylko do przygotowania plików. Licencja programu nie obejmuje tego, co się w nim tworzy.
- Pakiet npm `geotiff` 2.1.3 (MIT) czyta pliki DEM tylko podczas przygotowania zasobów i nie trafia do gry.
