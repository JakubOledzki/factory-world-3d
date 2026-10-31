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

## Copernicus DEM GLO-30

Numeryczny model terenu (30 m), z którego powstaje prawdziwe ukształtowanie Tatr wokół działki. Kafle `N49_00_E019_00` i `N49_00_E020_00` są pobierane z https://copernicus-dem-30m.s3.amazonaws.com.

Licencja: dane Copernicus DEM GLO-30 są bezpłatne do każdego celu, także komercyjnego, pod warunkiem podania źródła. Wymagana informacja (do napisów końcowych gry):

> © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.

Opis danych i licencji: https://registry.opendata.aws/copernicus-dem/

## Narzędzia

- Blender 5.2 (GPL) służy tylko do przygotowania plików. Licencja programu nie obejmuje tego, co się w nim tworzy.
- Pakiet npm `geotiff` 2.1.3 (MIT) czyta pliki DEM tylko podczas przygotowania zasobów i nie trafia do gry.
