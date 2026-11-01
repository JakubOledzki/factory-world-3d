namespace FactoryWorld.Modules.Abstractions.Contracts;

/// <summary>Every item in the game. Ids are stored in the database, so never rename one.</summary>
public static class Items
{
    public const string Trash = "trash";
    public const string WastePaper = "waste_paper";
    public const string WasteAluminium = "waste_aluminium";
    public const string WasteCopper = "waste_copper";
    public const string WastePlastic = "waste_plastic";
    public const string WasteGlass = "waste_glass";

    // Furniture: stands in the garage, can be picked up into the inventory and placed again.
    public const string TrashBin = "furniture_trash_bin";
    public const string Workbench = "furniture_workbench";
    public const string Shelf = "furniture_shelf";
    public const string BarrelBlue = "furniture_barrel_blue";
    public const string BarrelRed = "furniture_barrel_red";
    public const string Tyres = "furniture_tyres";
    public const string Pallet = "furniture_pallet";
    public const string GasCylinder = "furniture_gas_cylinder";
    public const string SortingMachine = "furniture_sorting_machine";
    public const string Furnace = "furniture_furnace";
    public const string Anvil = "furniture_anvil";

    // A wardrobe in the garage keeps clothes and saved outfits to change into at once.
    public const string ClothesWardrobe = "furniture_wardrobe";
    /// <summary>Where research is done; every garage has one from the start.</summary>
    public const string ResearchTable = "furniture_research_table";

    // Upgraded furnaces: they stand on plots only and smelt faster, holding more.
    public const string BrickFurnace = "furniture_brick_furnace";
    public const string BlastFurnace = "furniture_blast_furnace";

    // Tools: held in the character's tool bar (one slot, more with a backpack).
    public const string Axe = "tool_axe";
    public const string Shovel = "tool_shovel";
    public const string Pickaxe = "tool_pickaxe";
    public const string Hoe = "tool_hoe";
    public const string Chainsaw = "tool_chainsaw";
    public const string Shears = "tool_shears";

    // Better pickaxes, forged at the anvil: they mine and break rocks faster than the iron one.
    public const string BronzePickaxe = "tool_bronze_pickaxe";
    public const string SteelPickaxe = "tool_steel_pickaxe";

    // Saws better than the chainsaw, researched and forged at the anvil: they cut trees faster and get more out of them.
    public const string SteelSaw = "tool_steel_saw";
    public const string HarvesterSaw = "tool_harvester_saw";
    public const string MeteoriteSaw = "tool_meteorite_saw";

    // Backpacks: worn by the character, each adds tiles to the inventory and slots to the tool bar.
    public const string SchoolBackpack = "backpack_school";
    public const string HikingBackpack = "backpack_hiking";
    public const string TrekkingBackpack = "backpack_trekking";
    public const string ExpeditionBackpack = "backpack_expedition";
    public const string TycoonBackpack = "backpack_tycoon";

    // Raw materials gathered on plots.
    public const string Wood = "wood";
    public const string Stone = "stone";
    public const string Sand = "sand";
    public const string Coal = "coal";
    public const string IronOre = "iron_ore";
    public const string CopperOre = "copper_ore";
    public const string TinOre = "tin_ore";
    public const string SilverOre = "silver_ore";
    public const string GoldOre = "gold_ore";

    // Smelted in a furnace from ores, and made at a workbench.
    public const string IronIngot = "iron_ingot";
    public const string CopperIngot = "copper_ingot";
    public const string TinIngot = "tin_ingot";
    public const string SilverIngot = "silver_ingot";
    public const string GoldIngot = "gold_ingot";
    public const string Nails = "nails";

    // Forestry: a sapling is planted on a plot and grows into a tree.
    public const string Sapling = "sapling";

    // Husbandry: animals let out on a pasture, fed crops, give eggs, wool (cut with shears) and milk.
    public const string Chicken = "animal_chicken";
    public const string Sheep = "animal_sheep";
    public const string Cow = "animal_cow";
    public const string Egg = "egg";
    public const string Wool = "wool";
    public const string Milk = "milk";

    // Crops grown on fertile plots.
    public const string Wheat = "wheat";
    public const string Carrot = "carrot";
    public const string Cabbage = "cabbage";
    public const string Corn = "corn";
    public const string Potato = "potato";
    public const string Sunflower = "sunflower";

    // Logistics on plots: belts carry items, inserters move them between belts, machines and storage.
    public const string Belt = "logistics_belt";
    public const string Inserter = "logistics_inserter";
    public const string Splitter = "logistics_splitter";

    // Upgraded belts and inserters: the same, only faster.
    public const string FastBelt = "logistics_fast_belt";
    public const string ExpressBelt = "logistics_express_belt";
    public const string FastInserter = "logistics_fast_inserter";
    public const string ExpressInserter = "logistics_express_inserter";

    // More logistics researched in its branch: a belt that runs under what stands on the tiles it passes,
    // an inserter that reaches two tiles each way, a faster cable car and drones that fly items between ports.
    public const string UndergroundBelt = "logistics_underground_belt";
    public const string LongInserter = "logistics_long_inserter";
    public const string ExpressCableStation = "logistics_express_cable_station";
    public const string DronePort = "logistics_drone_port";

    // Storage put on plots: chests take anything, piles, heaps and silos one kind of bulk goods.
    public const string WoodenChest = "storage_wooden_chest";
    public const string IronChest = "storage_iron_chest";
    public const string WoodPile = "storage_wood_pile";
    public const string Heap = "storage_heap";
    public const string GrainSilo = "storage_grain_silo";
    public const string LogisticsDepot = "storage_logistics_depot";

    // Food processing: machines put on a plot turn crops into food, step by step.
    public const string Mill = "furniture_mill";
    /// <summary>The mill's higher levels, bigger and faster, grinding more flour out of the same grain.</summary>
    public const string RollerMill = "furniture_roller_mill";
    public const string FlourWorks = "furniture_flour_works";
    public const string OilPress = "furniture_oil_press";
    public const string PicklingBarrel = "furniture_pickling_barrel";
    public const string Bakery = "furniture_bakery";
    public const string Kitchen = "furniture_kitchen";
    public const string Flour = "flour";
    public const string CornFlour = "corn_flour";
    public const string SunflowerOil = "sunflower_oil";
    public const string Sauerkraut = "sauerkraut";
    public const string Bread = "bread";
    public const string CornBread = "corn_bread";
    public const string CarrotCake = "carrot_cake";
    public const string Fries = "fries";
    public const string VegetableSoup = "vegetable_soup";
    public const string Bigos = "bigos";
    public const string ScrambledEggs = "scrambled_eggs";

    // Office: a computer put on a plot opens the market there, far from the garage.
    public const string Computer = "furniture_computer";

    // Power on plots: a generator burns wood or coal, poles carry its power to machines near them.
    public const string Generator = "power_generator";
    public const string Pole = "power_pole";

    // Forestry industry: a forester's building stands on 3 × 3 tiles of a plot, plants saplings around
    // itself and cuts the grown trees by itself; a sawmill works the wood. Each comes in three levels.
    public const string ForesterHut = "furniture_forester_hut";
    public const string ForesterLodge = "furniture_forester_lodge";
    public const string ForestryStation = "furniture_forestry_station";
    public const string Sawmill = "furniture_sawmill";
    public const string ElectricSawmill = "furniture_electric_sawmill";
    public const string IndustrialSawmill = "furniture_industrial_sawmill";

    /// <summary>Assembling machines: they make the workbench's recipes by themselves, from what inserters load into them, larger and faster by level.</summary>
    public const string Assembler = "furniture_assembler";
    public const string FastAssembler = "furniture_fast_assembler";
    public const string ExpressAssembler = "furniture_express_assembler";
    public const string Planks = "planks";
    public const string Beams = "beams";
    public const string WoodPellets = "wood_pellets";
    public const string Plywood = "plywood";

    // Mountain industry: minerals some mountain plots have in their mines (a geological survey tells which),
    // ore crushed for more ingots, alloys from the foundry, stonework, dynamite and props for deep mines.
    public const string Sulfur = "sulfur";
    public const string Salt = "salt";
    public const string Marble = "marble";
    public const string Quartz = "quartz";
    public const string CrushedIronOre = "crushed_iron_ore";
    public const string CrushedCopperOre = "crushed_copper_ore";
    public const string CrushedTinOre = "crushed_tin_ore";
    public const string BronzeIngot = "bronze_ingot";
    public const string SteelIngot = "steel_ingot";
    public const string StoneBlocks = "stone_blocks";
    public const string MarbleSlabs = "marble_slabs";
    public const string Bricks = "bricks";
    public const string Dynamite = "dynamite";
    public const string MineSupport = "mine_support";

    // Mountain machines: a drill stands at a mine and digs it by itself, a quarry cuts stone, a crusher
    // grinds stone and ore, a stonemason dresses stone and marble, a foundry casts alloys and fires bricks.
    public const string Drill = "furniture_drill";
    public const string HeavyDrill = "furniture_heavy_drill";
    public const string Quarry = "furniture_quarry";
    public const string Crusher = "furniture_crusher";
    public const string Stonemason = "furniture_stonemason";
    public const string Foundry = "furniture_foundry";

    // Higher levels of the quarry and the crusher, bigger and on power: they work faster and get more out of the same stone and ore.
    public const string MechanizedQuarry = "furniture_mechanized_quarry";
    public const string OpenPitMine = "furniture_open_pit_mine";
    public const string ConeCrusher = "furniture_cone_crusher";
    public const string OreProcessingPlant = "furniture_ore_processing_plant";

    // Water power: a water wheel and a small hydro plant give power without fuel, by a river or the sea.
    public const string WaterWheel = "power_water_wheel";
    public const string HydroPlant = "power_hydro_plant";

    // A cable car: stations facing the same way carry what a belt brings them over anything in between.
    public const string CableStation = "logistics_cable_station";

    // Coastal plots: the sea along the coast and a lake on the lakeshore, fished with a rod or from boats;
    // fish smoked, salted and canned, salt boiled from the sea, salmon and mussels farmed, and the beach
    // combed for what the sea throws up.
    public const string FishingRod = "tool_fishing_rod";
    public const string Herring = "herring";
    public const string Cod = "cod";
    public const string Carp = "carp";
    public const string Pike = "pike";
    public const string Salmon = "salmon";
    public const string Mussels = "mussels";
    public const string Seaweed = "seaweed";
    public const string Amber = "amber";
    public const string SmokedFish = "smoked_fish";
    public const string SmokedSalmon = "smoked_salmon";
    public const string SaltedHerring = "salted_herring";
    public const string CannedFish = "canned_fish";
    public const string FishMeal = "fish_meal";
    public const string FishOil = "fish_oil";
    public const string FriedCarp = "fried_carp";
    public const string FishSoup = "fish_soup";

    // Coastal buildings: they stand on the shore, right next to the water.
    public const string FishingBoat = "furniture_fishing_boat";
    public const string RowingBoat = "furniture_rowing_boat";
    public const string Smokehouse = "furniture_smokehouse";
    public const string Cannery = "furniture_cannery";
    public const string SaltPan = "furniture_salt_pan";
    public const string FishFarm = "furniture_fish_farm";
    public const string Shipyard = "furniture_shipyard";
    public const string Port = "furniture_port";
    public const string Breakwater = "furniture_breakwater";
    public const string Lighthouse = "furniture_lighthouse";
    public const string TidalPlant = "power_tidal_plant";

    // Oil and gas: wells pump what the survey found under a plot, pipes carry the fluids, a distillery and a refinery
    // split crude oil into fuels and asphalt, a cracker makes more petrol of the heavier ones and a chemical plant
    // makes plastic, rubber and fertiliser of gas.
    public const string CrudeOil = "crude_oil";
    public const string NaturalGas = "natural_gas";
    public const string Gasoline = "gasoline";
    public const string Diesel = "diesel";
    public const string Kerosene = "kerosene";
    public const string Asphalt = "asphalt";
    public const string Plastic = "plastic";
    public const string SyntheticRubber = "synthetic_rubber";
    public const string Fertilizer = "fertilizer";
    public const string Gasket = "gasket";

    public const string Pumpjack = "furniture_pumpjack";
    public const string GasWell = "furniture_gas_well";
    public const string OffshorePlatform = "furniture_offshore_platform";
    public const string WaterInjector = "furniture_water_injector";
    public const string OilSeparator = "furniture_oil_separator";
    public const string Distillery = "furniture_distillery";
    public const string Refinery = "furniture_refinery";
    public const string Cracker = "furniture_cracker";
    public const string ChemicalPlant = "furniture_chemical_plant";
    public const string GasPlant = "power_gas_plant";
    public const string DieselGenerator = "power_diesel_generator";
    public const string Pipe = "logistics_pipe";
    public const string Pump = "logistics_pump";
    public const string Tank = "storage_tank";
    public const string Road = "logistics_road";

    // A truck depot: its trucks drive goods on diesel from the plot's station to a station not far away, without the rail fee.
    public const string TruckDepot = "furniture_truck_depot";

    // A scrubber takes some of the smoke of the plot's furnaces, generators and kilns away (see the world's smog).
    public const string Scrubber = "furniture_scrubber";

    // The desert: glass from sand, silicon and solar power, water drawn or made, sun-dried bricks and concrete,
    // dates from an oasis, camels, and fossils the dunes give up now and then.
    public const string Glass = "glass";
    public const string WindowPane = "window_pane";
    public const string GlassBottles = "glass_bottles";
    public const string OpticalFiber = "optical_fiber";
    public const string Silicon = "silicon";
    public const string SiliconWafer = "silicon_wafer";
    public const string Water = "water";
    public const string Clay = "clay";
    public const string AdobeBricks = "adobe_bricks";
    public const string Cement = "cement";
    public const string Concrete = "concrete";
    public const string Dates = "dates";
    public const string Mirror = "mirror";
    public const string Ammonite = "ammonite";
    public const string AncientCoin = "ancient_coin";
    public const string DinosaurBone = "dinosaur_bone";

    // The trophy case: in the garage and on plots it shows its owner's badges and trophies to whoever looks.
    public const string TrophyCase = "furniture_trophy_case";

    // Seasonal treats that turn up while gathering during a holiday, and the decorations they buy.
    public const string Candy = "candy";
    public const string Bauble = "bauble";
    public const string PumpkinLantern = "decor_pumpkin_lantern";
    public const string Scarecrow = "decor_scarecrow";
    public const string ChristmasTree = "decor_christmas_tree";
    public const string Snowman = "decor_snowman";

    // Souvenirs that expeditions bring back now and then: for the album, not for the trader.
    public const string MammothTusk = "souvenir_mammoth_tusk";
    public const string DesertRose = "souvenir_desert_rose";
    public const string Edelweiss = "souvenir_edelweiss";
    public const string Nautilus = "souvenir_nautilus";
    public const string Totem = "souvenir_totem";
    public const string GoldenCompass = "souvenir_golden_compass";

    public const string Glassworks = "furniture_glassworks";
    public const string ArcFurnace = "furniture_arc_furnace";
    public const string WaterWell = "furniture_water_well";
    public const string Desalinator = "furniture_desalinator";
    public const string DryingYard = "furniture_drying_yard";
    public const string CementKiln = "furniture_cement_kiln";
    public const string ConcreteMixer = "furniture_concrete_mixer";
    public const string ReedFence = "furniture_reed_fence";
    public const string SolarPanel = "power_solar_panel";
    public const string SolarTower = "power_solar_tower";
    public const string Battery = "power_battery";
    public const string Camel = "animal_camel";

    // The glacier: wind turbines, ice cut and melted, a cold store, a server room and a polar station that speed research up,
    // meteorites, heaters against the frost, tourists who come for the aurora, dog sleds and snowmobiles, and bridges over crevasses.
    public const string Ice = "ice";
    public const string MeteoricIron = "meteoric_iron";
    public const string Iridium = "iridium";
    public const string MeteoritePickaxe = "tool_meteorite_pickaxe";
    public const string FrozenFish = "frozen_fish";
    public const string FrozenSalmon = "frozen_salmon";
    public const string IceCream = "ice_cream";
    public const string ComputingPower = "computing_power";
    public const string IceCore = "ice_core";
    public const string ResearchData = "research_data";
    public const string Takings = "tourist_takings";

    public const string WindTurbine = "power_wind_turbine";
    public const string LargeWindTurbine = "power_large_wind_turbine";
    public const string Heater = "power_heater";
    public const string IceMelter = "furniture_ice_melter";
    public const string ColdStore = "furniture_cold_store";
    public const string ServerRoom = "furniture_server_room";
    public const string IceCoreDrill = "furniture_ice_core_drill";
    public const string PolarStation = "furniture_polar_station";
    public const string IceHotel = "furniture_ice_hotel";
    public const string Viewpoint = "furniture_viewpoint";
    public const string DogKennel = "furniture_dog_kennel";
    public const string SnowmobileGarage = "furniture_snowmobile_garage";
    public const string TrackFoundation = "furniture_track_foundation";

    // Cities: an assembly hall puts goods of every biome together, the towns of the map want goods and grow on them,
    // players keep shops, high-bay stores, multi-storey factories, municipal sorting, freight terminals, company
    // headquarters and offices that sell their services, and a town hall where the town's players elect a mayor.
    public const string Bicycle = "bicycle";
    public const string CircuitBoard = "circuit_board";
    public const string ElectricMotor = "electric_motor";
    public const string Radio = "radio";
    public const string WashingMachine = "washing_machine";
    public const string Car = "car";
    public const string Blueprint = "blueprint";

    public const string AssemblyHall = "furniture_assembly_hall";
    public const string MultistoreyFactory = "furniture_multistorey_factory";
    public const string Shop = "furniture_shop";
    public const string HighBay = "storage_high_bay";
    public const string CitySorter = "furniture_city_sorter";
    public const string FreightTerminal = "furniture_freight_terminal";
    public const string CompanyHq = "furniture_company_hq";
    public const string DesignOffice = "furniture_design_office";
    public const string Laboratory = "furniture_laboratory";
    public const string ServiceWorkshop = "furniture_service_workshop";

    // Export by rail: a chest by the station whose goods a train takes every hour to a buying market in a town, which lists
    // them on the players' market; across the sea the train goes on a ferry from a rail port (see ExportRules in World).
    public const string ExportChest = "furniture_export_chest";
    public const string BuyMarket = "furniture_buy_market";
    public const string RailPort = "furniture_rail_port";

    // Freight between the player's own plots (see FreightRules in World): a train of a route takes the goods out of the loading
    // chests of one plot once they hold all it should carry, and puts them into the receiving chests of another. Four sizes each,
    // each researched; they are storages, so inserters fill and empty them and a filter keeps them to one item.
    public const string LoadingChest = "storage_loading_chest";
    public const string LoadingChest2 = "storage_loading_chest_2";
    public const string LoadingChest3 = "storage_loading_chest_3";
    public const string LoadingChest4 = "storage_loading_chest_4";
    public const string ReceivingChest = "storage_receiving_chest";
    public const string ReceivingChest2 = "storage_receiving_chest_2";
    public const string ReceivingChest3 = "storage_receiving_chest_3";
    public const string ReceivingChest4 = "storage_receiving_chest_4";

    // Banks of companies in towns: players keep money on accounts there, and it takes as long to get in or out as the way
    // from their nearest plot to the nearest of the bank's buildings (see BankRules in World).
    public const string Bank = "furniture_bank";
    public const string BankBranch = "furniture_bank_branch";

    // Clothes: sewn at a tailor's in town and worn in four slots; each makes some industry's work go faster
    // (or, for trade, makes the trader pay more), see Clothes.
    public const string Tailor = "furniture_tailor";
    public const string MinerHelmet = "clothes_miner_helmet";
    public const string SouWester = "clothes_sou_wester";
    public const string StrawHat = "clothes_straw_hat";
    public const string Beanie = "clothes_beanie";
    public const string FlannelJacket = "clothes_flannel_jacket";
    public const string SmithApron = "clothes_smith_apron";
    public const string Oilskin = "clothes_oilskin";
    public const string SuitJacket = "clothes_suit_jacket";
    public const string Overalls = "clothes_overalls";
    public const string WorkTrousers = "clothes_work_trousers";
    public const string SuitTrousers = "clothes_suit_trousers";
    public const string RubberBoots = "clothes_rubber_boots";
    public const string WorkBoots = "clothes_work_boots";
    public const string DressShoes = "clothes_dress_shoes";

    // Forestry: trees of five kinds with their own saplings, what the forest floor gives, paper, furniture, charcoal,
    // resin and what is made of it, honey, and the fight against fires and the bark beetle.
    public const string SaplingSpruce = "sapling_spruce";
    public const string SaplingBirch = "sapling_birch";
    public const string SaplingOak = "sapling_oak";
    public const string SaplingLinden = "sapling_linden";
    public const string OakWood = "oak_wood";
    public const string Resin = "resin";
    public const string Pulp = "pulp";
    public const string Paper = "paper";
    public const string Cardboard = "cardboard";
    public const string Chair = "chair";
    public const string Table = "table";
    public const string Wardrobe = "wardrobe";
    public const string OakDresser = "oak_dresser";
    public const string Charcoal = "charcoal";
    public const string Mushrooms = "mushrooms";
    public const string Berries = "berries";
    public const string Herbs = "herbs";
    public const string MushroomSoup = "mushroom_soup";
    public const string BerryPierogi = "berry_pierogi";
    public const string HerbalTea = "herbal_tea";
    public const string Honey = "honey";
    public const string Wax = "wax";
    public const string Gingerbread = "gingerbread";
    public const string Candles = "candles";
    public const string Tar = "tar";
    public const string Turpentine = "turpentine";
    public const string Rosin = "rosin";
    public const string Insecticide = "insecticide";

    public const string PaperMill = "furniture_paper_mill";
    public const string Carpentry = "furniture_carpentry";
    public const string CharcoalKiln = "furniture_charcoal_kiln";
    public const string TarKiln = "furniture_tar_kiln";
    public const string Beehive = "furniture_beehive";
    public const string Watchtower = "furniture_fire_watchtower";
    public const string FireStation = "furniture_fire_station";

    // The crafting tree of eleven levels: half-products that join branches of production (level 1), parts made of them
    // (levels 2-6), and at the top rockets, satellites and fusion (levels 7-9). Each level is made of the ones below it.
    public const string Lubricant = "lubricant";
    public const string ElectricWire = "electric_wire";
    public const string GlassLaminate = "glass_laminate";
    public const string Paint = "paint";
    public const string CaseinGlue = "casein_glue";
    public const string InsulationBoard = "insulation_board";
    public const string Firebrick = "firebrick";
    public const string PrecastConcrete = "precast_concrete";
    public const string Coolant = "coolant";
    public const string IridiumAlloy = "iridium_alloy";
    public const string Provisions = "provisions";
    public const string Bearing = "bearing";
    public const string PaintedSheet = "painted_sheet";
    public const string Coil = "coil";
    public const string WiringHarness = "wiring_harness";
    public const string RefractoryLining = "refractory_lining";
    public const string CarbonComposite = "carbon_composite";
    public const string SuperconductingWire = "superconducting_wire";
    public const string RocketFuel = "rocket_fuel";
    public const string Gearbox = "gearbox";
    public const string Rotor = "rotor";
    public const string MachineHousing = "machine_housing";
    public const string CombustionChamber = "combustion_chamber";
    public const string CompositePanel = "composite_panel";
    public const string SuperconductingMagnet = "superconducting_magnet";
    public const string Alternator = "alternator";
    public const string ControlModule = "control_module";
    public const string HydraulicActuator = "hydraulic_actuator";
    public const string RocketNozzle = "rocket_nozzle";
    public const string PressureVessel = "pressure_vessel";
    public const string ComputingCore = "computing_core";
    public const string RobotArm = "robot_arm";
    public const string Turbopump = "turbopump";
    public const string TurbineSet = "turbine_set";
    public const string CryogenicTank = "cryogenic_tank";
    public const string IndustrialRobot = "industrial_robot";
    public const string NavigationSystem = "navigation_system";
    public const string RocketEngine = "rocket_engine";
    public const string PlasmaChamber = "plasma_chamber";
    public const string Avionics = "avionics";
    public const string PlasmaControl = "plasma_control";
    public const string RocketStage = "rocket_stage";
    public const string Satellite = "satellite";
    public const string FusionReactor = "fusion_reactor";
    public const string LaunchRocket = "launch_rocket";
    public const string MachineShop = "furniture_machine_shop";
    public const string RoboticHall = "furniture_robotic_hall";
    public const string OxygenConverter = "furniture_oxygen_converter";
    public const string RollingMill = "furniture_rolling_mill";
    public const string ControlCenter = "furniture_control_center";
    public const string ContinuousCaster = "furniture_continuous_caster";
    public const string AutomatedWarehouse = "storage_automated_warehouse";
    public const string SteelCombine = "furniture_steel_combine";
    public const string Megafactory = "furniture_megafactory";
    public const string FusionPlant = "power_fusion_plant";

    // Bigger power: a substation and a high-voltage pylon carry power farther than a pole, a coal power plant and a nuclear one
    // give a lot of it and an energy store keeps it; uranium ore from deep mines goes into fuel rods for the nuclear plant.
    public const string Transformer = "transformer";
    public const string Substation = "power_substation";
    public const string Pylon = "power_pylon";
    public const string CoalPlant = "power_coal_plant";
    public const string EnergyStore = "power_energy_store";
    public const string UraniumOre = "uranium_ore";
    public const string FuelRod = "fuel_rod";
    public const string NuclearPlant = "power_nuclear_plant";

    // A grid tie joins a plot's network to a cable to a tie on another plot: power goes along it, for a price the supplier sets.
    public const string GridTie = "power_grid_tie";

    // Husbandry and gastronomy for the other industries: a slaughterhouse, a dairy, a potato distillery, a biogas plant and work horses
    // give what the rest of the game needs; meals feed the character, a works canteen feeds the machines' crews, fine dishes feed banquets.
    public const string Meat = "meat";
    public const string Hide = "hide";
    public const string Tallow = "tallow";
    public const string Bones = "bones";
    public const string Manure = "manure";
    public const string Gelatin = "gelatin";
    public const string CultureMedium = "culture_medium";
    public const string Cheese = "cheese";
    public const string Butter = "butter";
    public const string Casein = "casein";
    public const string Ethanol = "ethanol";
    public const string Stillage = "stillage";
    public const string DriedMeat = "dried_meat";
    public const string WorkersLunch = "workers_lunch";
    public const string CheeseSandwich = "cheese_sandwich";
    public const string MinersBreakfast = "miners_breakfast";
    public const string LumberjackGoulash = "lumberjack_goulash";
    public const string ConcentrateFeed = "concentrate_feed";
    public const string RoastBeef = "roast_beef";
    public const string Cheesecake = "cheesecake";
    public const string DiningTakings = "dining_takings";
    public const string Horse = "animal_horse";
    public const string LeatherBoots = "clothes_leather_boots";

    public const string Canteen = "furniture_canteen";
    public const string Slaughterhouse = "furniture_slaughterhouse";
    public const string Dairy = "furniture_dairy";
    public const string PotatoDistillery = "furniture_potato_distillery";
    public const string BiogasPlant = "furniture_biogas_plant";
    public const string Stable = "furniture_stable";
    public const string Bellows = "furniture_bellows";

    // A feeder stands by the pasture and feeds the hungry animals around it from the feed put in by hand or by an inserter.
    public const string Feeder = "furniture_feeder";

    // A farmer's farmstead stands on 3 × 3 tiles of a fertile plot, sows the crop it is told around itself and reaps it by itself; the manor farm is its upgrade.
    public const string Farmstead = "furniture_farmstead";
    public const string ManorFarm = "furniture_manor_farm";

    // The raw-material branches grown further. Mining: coke of coal and the precious metals crushed and worked into jewellery.
    public const string Coke = "coke";
    public const string CrushedSilverOre = "crushed_silver_ore";
    public const string CrushedGoldOre = "crushed_gold_ore";
    public const string Jewelry = "jewelry";
    public const string Silverware = "silverware";
    public const string CokeOven = "furniture_coke_oven";
    public const string Jeweler = "furniture_jeweler";

    // Forestry: a tree nursery raises the saplings of the foresters around it.
    public const string TreeNursery = "furniture_tree_nursery";

    // Husbandry: wool spun into yarn and woven into cloth, hemp twisted into rope and flax woven into linen; an automatic barn that
    // feeds the animals and collects what they give; pigs and goats.
    public const string Yarn = "yarn";
    public const string Cloth = "cloth";
    public const string Linen = "linen";
    public const string Rope = "rope";
    public const string Pig = "animal_pig";
    public const string Goat = "animal_goat";
    public const string SpinningMill = "furniture_spinning_mill";
    public const string Loom = "furniture_loom";
    public const string AutoBarn = "furniture_auto_barn";

    // Farming: new crops (sugar beets, flax, hemp) and sugar of the beets, a sprinkler and the mechanized farm, the farmstead's third level.
    public const string SugarBeet = "sugar_beet";
    public const string Flax = "flax";
    public const string Hemp = "hemp";
    public const string Sugar = "sugar";
    public const string BeetPulp = "beet_pulp";
    public const string Donuts = "donuts";
    public const string SugarFactory = "furniture_sugar_factory";
    public const string Sprinkler = "furniture_sprinkler";
    public const string MechanizedFarm = "furniture_mechanized_farm";

    // Energy: power of biomass, a combined heat and power plant, geothermal power, and hydrogen made of water and burnt in a fuel cell.
    public const string Hydrogen = "hydrogen";
    public const string BiomassPlant = "power_biomass_plant";
    public const string ChpPlant = "power_chp_plant";
    public const string GeothermalPlant = "power_geothermal_plant";
    public const string Electrolyzer = "furniture_electrolyzer";
    public const string FuelCell = "power_fuel_cell";

    /// <summary>Display names in Polish, keyed by id.</summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [Trash] = "Śmieci",
        [WastePaper] = "Makulatura",
        [WasteAluminium] = "Złom aluminium",
        [WasteCopper] = "Złom miedzi",
        [WastePlastic] = "Plastik z odzysku",
        [WasteGlass] = "Stłuczka szklana",
        [TrashBin] = "Kosz na śmieci",
        [Workbench] = "Stół warsztatowy",
        [Shelf] = "Regał",
        [ClothesWardrobe] = "Szafa na ubrania",
        [BarrelBlue] = "Niebieska beczka",
        [BarrelRed] = "Czerwona beczka",
        [Tyres] = "Opony",
        [Pallet] = "Paleta",
        [GasCylinder] = "Butla gazowa",
        [SortingMachine] = "Automatyczna sortownia",
        [Furnace] = "Piec kamienny",
        [Anvil] = "Kowadło",
        [ResearchTable] = "Stół badawczy",
        [BrickFurnace] = "Piec ceglany",
        [BlastFurnace] = "Piec hutniczy",
        [Axe] = "Siekiera",
        [Shovel] = "Łopata",
        [Pickaxe] = "Kilof",
        [Hoe] = "Motyka",
        [Chainsaw] = "Piła spalinowa",
        [Shears] = "Nożyce do strzyżenia",
        [SchoolBackpack] = "Plecak szkolny",
        [HikingBackpack] = "Plecak turystyczny",
        [TrekkingBackpack] = "Plecak trekkingowy",
        [ExpeditionBackpack] = "Plecak wyprawowy",
        [TycoonBackpack] = "Plecak magnata",
        [Wood] = "Drewno",
        [Stone] = "Kamień",
        [Sand] = "Piasek",
        [Coal] = "Węgiel",
        [IronOre] = "Ruda żelaza",
        [CopperOre] = "Ruda miedzi",
        [TinOre] = "Ruda cyny",
        [SilverOre] = "Ruda srebra",
        [GoldOre] = "Ruda złota",
        [IronIngot] = "Sztabka żelaza",
        [CopperIngot] = "Sztabka miedzi",
        [TinIngot] = "Sztabka cyny",
        [SilverIngot] = "Sztabka srebra",
        [GoldIngot] = "Sztabka złota",
        [Nails] = "Gwoździe",
        [Sapling] = "Sadzonka sosny",
        [Chicken] = "Kura",
        [Sheep] = "Owca",
        [Cow] = "Krowa",
        [Egg] = "Jajka",
        [Wool] = "Wełna",
        [Milk] = "Mleko",
        [Wheat] = "Pszenica",
        [Carrot] = "Marchew",
        [Cabbage] = "Kapusta",
        [Corn] = "Kukurydza",
        [Potato] = "Ziemniaki",
        [Sunflower] = "Słonecznik",
        [Belt] = "Taśmociąg",
        [Inserter] = "Podajnik",
        [Splitter] = "Rozdzielacz",
        [FastBelt] = "Szybki taśmociąg",
        [ExpressBelt] = "Ekspresowy taśmociąg",
        [FastInserter] = "Szybki podajnik",
        [ExpressInserter] = "Ekspresowy podajnik",
        [UndergroundBelt] = "Taśmociąg podziemny",
        [LongInserter] = "Długoramienny podajnik",
        [ExpressCableStation] = "Ekspresowa stacja kolejki linowej",
        [DronePort] = "Port dronów",
        [WoodenChest] = "Skrzynia drewniana",
        [IronChest] = "Skrzynia żelazna",
        [WoodPile] = "Stos drewna",
        [Heap] = "Hałda",
        [GrainSilo] = "Silos zbożowy",
        [LogisticsDepot] = "Magazyn logistyczny",
        [Mill] = "Młyn",
        [RollerMill] = "Młyn walcowy",
        [FlourWorks] = "Zakłady młyńskie",
        [OilPress] = "Tłocznia oleju",
        [PicklingBarrel] = "Beczka do kiszenia",
        [Bakery] = "Piec piekarski",
        [Computer] = "Komputer",
        [Kitchen] = "Kuchnia polowa",
        [Flour] = "Mąka pszenna",
        [CornFlour] = "Mąka kukurydziana",
        [SunflowerOil] = "Olej słonecznikowy",
        [Sauerkraut] = "Kapusta kiszona",
        [Bread] = "Chleb",
        [CornBread] = "Chleb kukurydziany",
        [CarrotCake] = "Ciasto marchewkowe",
        [Fries] = "Frytki",
        [VegetableSoup] = "Zupa jarzynowa",
        [Bigos] = "Bigos",
        [ScrambledEggs] = "Jajecznica",
        [Generator] = "Generator parowy",
        [Pole] = "Słup energetyczny",
        [ForesterHut] = "Chata leśnika",
        [ForesterLodge] = "Leśniczówka",
        [ForestryStation] = "Nadleśnictwo",
        [Sawmill] = "Tartak",
        [ElectricSawmill] = "Tartak elektryczny",
        [IndustrialSawmill] = "Tartak przemysłowy",
        [Assembler] = "Stacja montażowa",
        [FastAssembler] = "Szybka stacja montażowa",
        [ExpressAssembler] = "Ekspresowa stacja montażowa",
        [Planks] = "Deski",
        [Beams] = "Belki",
        [WoodPellets] = "Pellet drzewny",
        [Plywood] = "Sklejka",
        [BronzePickaxe] = "Kilof z brązu",
        [SteelPickaxe] = "Kilof stalowy",
        [Sulfur] = "Siarka",
        [Salt] = "Sól",
        [Marble] = "Marmur",
        [Quartz] = "Kwarc",
        [CrushedIronOre] = "Kruszona ruda żelaza",
        [CrushedCopperOre] = "Kruszona ruda miedzi",
        [CrushedTinOre] = "Kruszona ruda cyny",
        [BronzeIngot] = "Sztabka brązu",
        [SteelIngot] = "Sztabka stali",
        [StoneBlocks] = "Bloki kamienne",
        [MarbleSlabs] = "Płyty marmurowe",
        [Bricks] = "Cegły",
        [Dynamite] = "Dynamit",
        [MineSupport] = "Obudowa górnicza",
        [Drill] = "Wiertło górnicze",
        [HeavyDrill] = "Wiertło udarowe",
        [Quarry] = "Kamieniołom",
        [Crusher] = "Kruszarka",
        [MechanizedQuarry] = "Kamieniołom zmechanizowany",
        [OpenPitMine] = "Kopalnia odkrywkowa",
        [ConeCrusher] = "Kruszarka stożkowa",
        [OreProcessingPlant] = "Zakład przeróbczy",
        [Stonemason] = "Warsztat kamieniarski",
        [Foundry] = "Odlewnia",
        [WaterWheel] = "Koło wodne",
        [HydroPlant] = "Mała elektrownia wodna",
        [CableStation] = "Stacja kolejki linowej",
        [FishingRod] = "Wędka",
        [Herring] = "Śledź",
        [Cod] = "Dorsz",
        [Carp] = "Karp",
        [Pike] = "Szczupak",
        [Salmon] = "Łosoś",
        [Mussels] = "Małże",
        [Seaweed] = "Algi",
        [Amber] = "Bursztyn",
        [SmokedFish] = "Ryba wędzona",
        [SmokedSalmon] = "Łosoś wędzony",
        [SaltedHerring] = "Śledzie w soli",
        [CannedFish] = "Konserwa rybna",
        [FishMeal] = "Mączka rybna",
        [FishOil] = "Olej rybny",
        [FriedCarp] = "Karp smażony",
        [FishSoup] = "Zupa rybna",
        [FishingBoat] = "Kuter rybacki",
        [RowingBoat] = "Łódź rybacka",
        [Smokehouse] = "Wędzarnia",
        [Cannery] = "Konserwiarnia",
        [SaltPan] = "Warzelnia soli",
        [FishFarm] = "Farma ryb",
        [Shipyard] = "Stocznia",
        [Port] = "Przystań",
        [Breakwater] = "Falochron",
        [Lighthouse] = "Latarnia morska",
        [TidalPlant] = "Elektrownia pływowa",
        [CrudeOil] = "Ropa naftowa",
        [NaturalGas] = "Gaz ziemny",
        [Gasoline] = "Benzyna",
        [Diesel] = "Olej napędowy",
        [Kerosene] = "Nafta",
        [Asphalt] = "Asfalt",
        [Plastic] = "Tworzywo sztuczne",
        [SyntheticRubber] = "Kauczuk syntetyczny",
        [Fertilizer] = "Nawóz",
        [Gasket] = "Uszczelka",
        [Pumpjack] = "Żuraw pompowy",
        [GasWell] = "Głowica gazowa",
        [OffshorePlatform] = "Platforma wiertnicza",
        [WaterInjector] = "Tłocznia wody",
        [OilSeparator] = "Separator ropy",
        [Distillery] = "Destylarnia",
        [Refinery] = "Rafineria",
        [Cracker] = "Instalacja krakingu",
        [ChemicalPlant] = "Zakład chemiczny",
        [GasPlant] = "Elektrownia gazowa",
        [DieselGenerator] = "Agregat spalinowy",
        [Pipe] = "Rura",
        [Pump] = "Pompa",
        [Tank] = "Zbiornik",
        [Road] = "Droga asfaltowa",
        [TruckDepot] = "Baza ciężarówek",
        [Scrubber] = "Filtr spalin",
        [Glass] = "Szkło",
        [WindowPane] = "Szyba",
        [GlassBottles] = "Butelki szklane",
        [OpticalFiber] = "Światłowód",
        [Silicon] = "Krzem",
        [SiliconWafer] = "Płytka krzemowa",
        [Water] = "Woda",
        [Clay] = "Glina",
        [AdobeBricks] = "Cegła suszona",
        [Cement] = "Cement",
        [Concrete] = "Beton",
        [Dates] = "Daktyle",
        [Mirror] = "Lustro",
        [Ammonite] = "Amonit",
        [AncientCoin] = "Starożytna moneta",
        [DinosaurBone] = "Kość dinozaura",
        [TrophyCase] = "Gablota z trofeami",
        [Candy] = "Cukierek",
        [Bauble] = "Bombka",
        [PumpkinLantern] = "Lampion z dyni",
        [Scarecrow] = "Strach na wróble",
        [ChristmasTree] = "Choinka",
        [Snowman] = "Bałwan",
        [MammothTusk] = "Kieł mamuta",
        [DesertRose] = "Róża pustyni",
        [Edelweiss] = "Szarotka w ramce",
        [Nautilus] = "Muszla łodzika",
        [Totem] = "Drewniany totem",
        [GoldenCompass] = "Złoty kompas",
        [Glassworks] = "Huta szkła",
        [ArcFurnace] = "Piec łukowy",
        [WaterWell] = "Studnia głębinowa",
        [Desalinator] = "Odsalarka",
        [DryingYard] = "Suszarnia cegieł",
        [CementKiln] = "Piec cementowy",
        [ConcreteMixer] = "Betoniarka",
        [ReedFence] = "Płot trzcinowy",
        [SolarPanel] = "Panel słoneczny",
        [SolarTower] = "Elektrownia słoneczna z lustrami",
        [Battery] = "Akumulator",
        [Camel] = "Wielbłąd",
        [Ice] = "Lód",
        [MeteoricIron] = "Żelazo meteorytowe",
        [Iridium] = "Iryd",
        [MeteoritePickaxe] = "Kilof meteorytowy",
        [SteelSaw] = "Piła stalowa",
        [HarvesterSaw] = "Piła harwesterowa",
        [MeteoriteSaw] = "Piła meteorytowa",
        [FrozenFish] = "Mrożona ryba",
        [FrozenSalmon] = "Mrożony łosoś",
        [IceCream] = "Lody",
        [ComputingPower] = "Moc obliczeniowa",
        [IceCore] = "Rdzeń lodowy",
        [ResearchData] = "Dane naukowe",
        [Takings] = "Utarg od turystów",
        [WindTurbine] = "Turbina wiatrowa",
        [LargeWindTurbine] = "Duża turbina wiatrowa",
        [Heater] = "Kocioł grzewczy",
        [IceMelter] = "Topielnik lodu",
        [ColdStore] = "Chłodnia",
        [ServerRoom] = "Serwerownia",
        [IceCoreDrill] = "Wiertnica lodowa",
        [PolarStation] = "Stacja polarna",
        [IceHotel] = "Hotel lodowy",
        [Viewpoint] = "Punkt widokowy",
        [DogKennel] = "Psiarnia z zaprzęgiem",
        [SnowmobileGarage] = "Garaż skuterów śnieżnych",
        [TrackFoundation] = "Fundament toru",
        [Bicycle] = "Rower",
        [CircuitBoard] = "Płytka drukowana",
        [ElectricMotor] = "Silnik elektryczny",
        [Radio] = "Radio",
        [WashingMachine] = "Pralka",
        [Car] = "Samochód",
        [Blueprint] = "Plany budynku",
        [AssemblyHall] = "Hala montażowa",
        [MultistoreyFactory] = "Fabryka wielopiętrowa",
        [Shop] = "Sklep",
        [HighBay] = "Magazyn wysokiego składowania",
        [CitySorter] = "Sortownia miejska",
        [FreightTerminal] = "Dworzec towarowy",
        [CompanyHq] = "Siedziba spółki",
        [DesignOffice] = "Biuro projektowe",
        [Laboratory] = "Laboratorium",
        [ServiceWorkshop] = "Warsztat serwisowy",
        [ExportChest] = "Skrzynia eksportowa",
        [BuyMarket] = "Rynek skupu",
        [RailPort] = "Port pociągowy",
        [LoadingChest] = "Skrzynia ładująca",
        [LoadingChest2] = "Skrzynia ładująca II",
        [LoadingChest3] = "Skrzynia ładująca III",
        [LoadingChest4] = "Skrzynia ładująca IV",
        [ReceivingChest] = "Skrzynia odbierająca",
        [ReceivingChest2] = "Skrzynia odbierająca II",
        [ReceivingChest3] = "Skrzynia odbierająca III",
        [ReceivingChest4] = "Skrzynia odbierająca IV",
        [Bank] = "Bank",
        [BankBranch] = "Oddział banku",
        [Tailor] = "Pracownia krawiecka",
        [MinerHelmet] = "Kask górniczy z czołówką",
        [SouWester] = "Kapelusz rybacki",
        [StrawHat] = "Słomkowy kapelusz",
        [Beanie] = "Czapka drwala",
        [FlannelJacket] = "Kurtka flanelowa",
        [SmithApron] = "Fartuch kowalski",
        [Oilskin] = "Sztormiak",
        [SuitJacket] = "Marynarka",
        [Overalls] = "Ogrodniczki",
        [WorkTrousers] = "Spodnie robocze",
        [SuitTrousers] = "Spodnie od garnituru",
        [RubberBoots] = "Kalosze",
        [WorkBoots] = "Trapery",
        [DressShoes] = "Półbuty",
        [SaplingSpruce] = "Sadzonka świerku",
        [SaplingBirch] = "Sadzonka brzozy",
        [SaplingOak] = "Sadzonka dębu",
        [SaplingLinden] = "Sadzonka lipy",
        [OakWood] = "Drewno dębowe",
        [Resin] = "Żywica",
        [Pulp] = "Masa celulozowa",
        [Paper] = "Papier",
        [Cardboard] = "Karton",
        [Chair] = "Krzesło",
        [Table] = "Stół",
        [Wardrobe] = "Szafa",
        [OakDresser] = "Komoda dębowa",
        [Charcoal] = "Węgiel drzewny",
        [Mushrooms] = "Grzyby",
        [Berries] = "Jagody",
        [Herbs] = "Zioła",
        [MushroomSoup] = "Zupa grzybowa",
        [BerryPierogi] = "Pierogi z jagodami",
        [HerbalTea] = "Herbata ziołowa",
        [Honey] = "Miód",
        [Wax] = "Wosk",
        [Gingerbread] = "Piernik",
        [Candles] = "Świece",
        [Tar] = "Smoła",
        [Turpentine] = "Terpentyna",
        [Rosin] = "Kalafonia",
        [Insecticide] = "Środek na kornika",
        [PaperMill] = "Papiernia",
        [Carpentry] = "Stolarnia",
        [CharcoalKiln] = "Mielerz",
        [TarKiln] = "Smolarnia",
        [Beehive] = "Ul",
        [Watchtower] = "Wieża obserwacyjna",
        [FireStation] = "Strażnica",
        [Lubricant] = "Smar techniczny",
        [ElectricWire] = "Przewód elektryczny",
        [GlassLaminate] = "Laminat szklany",
        [Paint] = "Farba",
        [CaseinGlue] = "Klej kazeinowy",
        [InsulationBoard] = "Płyta izolacyjna",
        [Firebrick] = "Cegła szamotowa",
        [PrecastConcrete] = "Prefabrykat żelbetowy",
        [Coolant] = "Płyn chłodniczy",
        [IridiumAlloy] = "Stop irydowy",
        [Provisions] = "Prowiant",
        [Bearing] = "Łożysko",
        [PaintedSheet] = "Blacha lakierowana",
        [Coil] = "Cewka",
        [WiringHarness] = "Wiązka kablowa",
        [RefractoryLining] = "Okładzina ogniotrwała",
        [CarbonComposite] = "Kompozyt węglowy",
        [SuperconductingWire] = "Drut nadprzewodzący",
        [RocketFuel] = "Paliwo rakietowe",
        [Gearbox] = "Przekładnia",
        [Rotor] = "Wirnik",
        [MachineHousing] = "Obudowa przemysłowa",
        [CombustionChamber] = "Komora spalania",
        [CompositePanel] = "Panel kompozytowy",
        [SuperconductingMagnet] = "Elektromagnes nadprzewodzący",
        [Alternator] = "Prądnica",
        [ControlModule] = "Moduł sterujący",
        [HydraulicActuator] = "Siłownik hydrauliczny",
        [RocketNozzle] = "Dysza rakietowa",
        [PressureVessel] = "Zbiornik ciśnieniowy",
        [ComputingCore] = "Rdzeń obliczeniowy",
        [RobotArm] = "Ramię robotyczne",
        [Turbopump] = "Turbopompa",
        [TurbineSet] = "Turbozespół",
        [CryogenicTank] = "Zbiornik kriogeniczny",
        [IndustrialRobot] = "Robot przemysłowy",
        [NavigationSystem] = "System nawigacji",
        [RocketEngine] = "Silnik rakietowy",
        [PlasmaChamber] = "Komora plazmowa",
        [Avionics] = "Awionika",
        [PlasmaControl] = "Układ sterowania plazmą",
        [RocketStage] = "Człon rakiety",
        [Satellite] = "Satelita",
        [FusionReactor] = "Reaktor fuzyjny",
        [LaunchRocket] = "Rakieta nośna",
        [MachineShop] = "Warsztat mechaniczny",
        [RoboticHall] = "Zrobotyzowana hala",
        [OxygenConverter] = "Konwerter tlenowy",
        [RollingMill] = "Walcownia",
        [ControlCenter] = "Centrum sterowania",
        [ContinuousCaster] = "Odlewnia ciągła",
        [AutomatedWarehouse] = "Magazyn zautomatyzowany",
        [SteelCombine] = "Kombinat hutniczy",
        [Megafactory] = "Megafabryka",
        [FusionPlant] = "Elektrownia fuzyjna",
        [Transformer] = "Transformator",
        [Substation] = "Podstacja",
        [Pylon] = "Słup wysokiego napięcia",
        [CoalPlant] = "Elektrownia węglowa",
        [EnergyStore] = "Magazyn energii",
        [UraniumOre] = "Ruda uranu",
        [FuelRod] = "Pręt paliwowy",
        [NuclearPlant] = "Elektrownia jądrowa",
        [GridTie] = "Przyłącze sieciowe",
        [Meat] = "Mięso",
        [Hide] = "Skóra",
        [Tallow] = "Łój",
        [Bones] = "Kości",
        [Manure] = "Obornik",
        [Gelatin] = "Żelatyna",
        [CultureMedium] = "Pożywka laboratoryjna",
        [Cheese] = "Ser",
        [Butter] = "Masło",
        [Casein] = "Kazeina",
        [Ethanol] = "Etanol",
        [Stillage] = "Wywar",
        [DriedMeat] = "Suszone mięso",
        [WorkersLunch] = "Obiad pracowniczy",
        [CheeseSandwich] = "Kanapka z serem",
        [MinersBreakfast] = "Drugie śniadanie górnika",
        [LumberjackGoulash] = "Gulasz drwala",
        [ConcentrateFeed] = "Pasza treściwa",
        [RoastBeef] = "Pieczeń wołowa",
        [Cheesecake] = "Sernik",
        [DiningTakings] = "Utarg z restauracji",
        [Horse] = "Koń roboczy",
        [LeatherBoots] = "Buty skórzane",
        [Canteen] = "Stołówka zakładowa",
        [Slaughterhouse] = "Rzeźnia",
        [Dairy] = "Mleczarnia",
        [PotatoDistillery] = "Gorzelnia",
        [BiogasPlant] = "Biogazownia",
        [Stable] = "Stajnia",
        [Feeder] = "Karmnik",
        [Bellows] = "Miech skórzany",
        [Farmstead] = "Zagroda rolnika",
        [ManorFarm] = "Folwark",
        [Coke] = "Koks",
        [CrushedSilverOre] = "Kruszona ruda srebra",
        [CrushedGoldOre] = "Kruszona ruda złota",
        [Jewelry] = "Biżuteria",
        [Silverware] = "Srebrna zastawa",
        [CokeOven] = "Koksownia",
        [Jeweler] = "Pracownia jubilerska",
        [TreeNursery] = "Szkółka leśna",
        [Yarn] = "Przędza",
        [Cloth] = "Sukno",
        [Linen] = "Płótno lniane",
        [Rope] = "Lina konopna",
        [Pig] = "Świnia",
        [Goat] = "Koza",
        [SpinningMill] = "Przędzalnia",
        [Loom] = "Krosno",
        [AutoBarn] = "Obora automatyczna",
        [SugarBeet] = "Burak cukrowy",
        [Flax] = "Len",
        [Hemp] = "Konopie",
        [Sugar] = "Cukier",
        [BeetPulp] = "Wysłodki",
        [Donuts] = "Pączki",
        [SugarFactory] = "Cukrownia",
        [Sprinkler] = "Zraszacz",
        [MechanizedFarm] = "Gospodarstwo zmechanizowane",
        [Hydrogen] = "Wodór",
        [BiomassPlant] = "Elektrownia na biomasę",
        [ChpPlant] = "Elektrociepłownia",
        [GeothermalPlant] = "Elektrownia geotermalna",
        [Electrolyzer] = "Elektrolizer",
        [FuelCell] = "Ogniwo paliwowe",
    };

    public static bool Exists(string id) => Names.ContainsKey(id);

    public static bool IsTool(string id) => id is Axe or Shovel or Pickaxe or Hoe or Chainsaw or Shears or BronzePickaxe or SteelPickaxe or FishingRod or MeteoritePickaxe
        || Saws.ContainsKey(id);

    /// <summary>Pickaxes of every kind and how many times faster than the iron one each mines and breaks rocks.</summary>
    public static readonly IReadOnlyDictionary<string, double> Pickaxes = new Dictionary<string, double>
    {
        [Pickaxe] = 1,
        [BronzePickaxe] = 1.25,
        [SteelPickaxe] = 1.5,
        [MeteoritePickaxe] = 2,
    };

    /// <summary>Saws of every kind, which do an axe's work: what part of an axe's time each takes over a tree, how much more wood it
    /// gets out of one, how many trees next to it it fells at the same cut, whether saplings fall twice as often and whether a tree
    /// the bark beetle is in still gives all its wood.</summary>
    public static readonly IReadOnlyDictionary<string, Saw> Saws = new Dictionary<string, Saw>
    {
        [Chainsaw] = new(0.3, 0, 0, false, false),
        [SteelSaw] = new(0.2, 1, 0, false, false),
        [HarvesterSaw] = new(0.2, 1, 2, true, false),
        [MeteoriteSaw] = new(0.1, 2, 4, true, true),
    };

    /// <summary>The best saw among <paramref name="held"/>, or null when there is none.</summary>
    public static string? BestSaw(IEnumerable<string> held) =>
        held.Where(Saws.ContainsKey).OrderBy(s => Saws[s].Time).ThenByDescending(s => Saws[s].Fells).FirstOrDefault();

    public static bool IsBackpack(string id) => Backpacks.ContainsKey(id);

    /// <summary>What each backpack gives while worn: extra inventory tiles and tool bar slots in all.</summary>
    public static readonly IReadOnlyDictionary<string, Backpack> Backpacks = new Dictionary<string, Backpack>
    {
        [SchoolBackpack] = new(20, 2),
        [HikingBackpack] = new(40, 2),
        [TrekkingBackpack] = new(60, 2),
        [ExpeditionBackpack] = new(80, 2),
        [TycoonBackpack] = new(100, 3),
    };

    /// <summary>Inventory tiles without a backpack.</summary>
    public const int InventorySlots = 20;

    /// <summary>Tool bar slots without a backpack.</summary>
    public const int ToolSlots = 1;

    /// <summary>Inventory tiles with the most a backpack adds.</summary>
    public static readonly int MaxInventorySlots = InventorySlots + Backpacks.Values.Max(b => b.ExtraSlots);

    /// <summary>Tool bar slots with the most a backpack gives.</summary>
    public static readonly int MaxToolSlots = Backpacks.Values.Max(b => b.ToolSlots);

    /// <summary>Furnaces of every level: the stone one and its upgrades.</summary>
    public static bool IsFurnace(string id) => id is Furnace or BrickFurnace or BlastFurnace;

    /// <summary>Assembling machines of every level.</summary>
    public static bool IsAssembler(string id) => id is Assembler or FastAssembler or ExpressAssembler;

    /// <summary>Forester's buildings of every level: the hut and its upgrades.</summary>
    public static bool IsForester(string id) => id is ForesterHut or ForesterLodge or ForestryStation;

    /// <summary>Farmer's buildings of every level: the farmstead and the manor farm.</summary>
    public static bool IsFarmstead(string id) => id is Farmstead or ManorFarm or MechanizedFarm;

    /// <summary>Fine dishes: what banquets, a company's listing, a mayor's festival and the restaurants of hotels need.</summary>
    public static bool IsFineDish(string id) => id is RoastBeef or Cheesecake;

    /// <summary>Mills of every level.</summary>
    public static bool IsMill(string id) => id is Mill or RollerMill or FlourWorks;

    /// <summary>Quarries of every level, which stand in the mountains only.</summary>
    public static bool IsQuarry(string id) => id is Quarry or MechanizedQuarry or OpenPitMine;

    /// <summary>Crushers of every level.</summary>
    public static bool IsCrusher(string id) => id is Crusher or ConeCrusher or OreProcessingPlant;

    /// <summary>Drills of every level, which stand at a mine and dig it by themselves.</summary>
    public static bool IsDrill(string id) => id is Drill or HeavyDrill;

    /// <summary>What gives power without fuel: the water wheel, the hydro plant and the tidal plant.</summary>
    public static bool IsHydro(string id) => id is WaterWheel or HydroPlant or TidalPlant;

    /// <summary>Boats, which fish the water their plot's shore stands on by themselves.</summary>
    public static bool IsBoat(string id) => id is FishingBoat or RowingBoat;

    /// <summary>Belts of every level (not splitters).</summary>
    public static bool IsBelt(string id) => id is Belt or FastBelt or ExpressBelt or Pipe;

    /// <summary>Inserters of every level.</summary>
    public static bool IsInserter(string id) => id is Inserter or FastInserter or ExpressInserter or LongInserter or Pump;

    /// <summary>Cable car stations of both levels.</summary>
    public static bool IsCableStation(string id) => id is CableStation or ExpressCableStation;

    /// <summary>Whatever items run along on a plot like on a belt: belts and pipes, splitters, underground belts and cable car stations.</summary>
    public static bool IsConveyor(string id) => IsBelt(id) || IsCableStation(id) || id is Splitter or UndergroundBelt;

    /// <summary>Fluids: they go only through pipes and pumps, never on belts or by inserters.</summary>
    public static bool IsFluid(string id) => id is CrudeOil or NaturalGas or Gasoline or Diesel or Kerosene or Water;

    /// <summary>What carries fluids: a pipe instead of a belt, a pump instead of an inserter.</summary>
    public static bool CarriesFluids(string kind) => kind is Pipe or Pump;

    /// <summary>Wells, which pump the oil or gas under their plot by themselves.</summary>
    public static bool IsWell(string id) => id is Pumpjack or GasWell or OffshorePlatform;

    /// <summary>Generators that burn fuel: the steam generator, the coal and gas power plants, the diesel generator, the nuclear plant,
    /// which burns fuel rods, and the fusion plant, which uses up coolant.</summary>
    public static bool IsGenerator(string id) => id is Generator or CoalPlant or GasPlant or DieselGenerator or NuclearPlant or FusionPlant or BiomassPlant
        or ChpPlant or FuelCell;

    /// <summary>What carries power on a plot: the pole, the substation, which powers more around it, and the high-voltage pylon, which is wired farther.</summary>
    public static bool IsPole(string id) => id is Pole or Substation or Pylon;

    /// <summary>What stores power: the battery and the energy store.</summary>
    public static bool IsBattery(string id) => id is Battery or EnergyStore;

    /// <summary>What turns sunlight into power: by day only, and less in a sandstorm.</summary>
    public static bool IsSolar(string id) => id is SolarPanel or SolarTower;

    /// <summary>What turns the wind into power: as much as the wind blows, nothing in a blizzard.</summary>
    public static bool IsWind(string id) => id is WindTurbine or LargeWindTurbine;

    /// <summary>What gives power without fuel: water power, the sun, the wind and the heat of the earth.</summary>
    public static bool IsRenewable(string id) => IsHydro(id) || IsSolar(id) || IsWind(id) || IsGeothermal(id);

    /// <summary>A geothermal plant gives its power all the time from the heat under the mountains and the ice.</summary>
    public static bool IsGeothermal(string id) => id == GeothermalPlant;

    /// <summary>A heater burns fuel to warm the machines around it against the frost of the glacier; it gives no power.</summary>
    public static bool IsHeater(string id) => id == Heater;

    /// <summary>What tourists and diners pay: taken out of a hotel or a viewpoint, it goes into the wallet, not the inventory.</summary>
    public static bool IsMoney(string id) => id is Takings or DiningTakings;

    /// <summary>What speeds research up, and by how many seconds each piece.</summary>
    public static readonly IReadOnlyDictionary<string, int> ResearchBoost = new Dictionary<string, int>
    {
        [ResearchData] = 60,
        [ComputingPower] = 15,
        [Blueprint] = 120,
        [CultureMedium] = 10,
    };

    /// <summary>What stands in a city only.</summary>
    public static bool IsCityOnly(string id) => id is MultistoreyFactory or Shop or HighBay or FreightTerminal or CompanyHq or DesignOffice or Laboratory or ServiceWorkshop
        or Tailor or BuyMarket or Bank or BankBranch or Assembler or FastAssembler or ExpressAssembler;

    /// <summary>A bank's building: its first one or a branch.</summary>
    public static bool IsBank(string id) => id is Bank or BankBranch;

    /// <summary>The chests a freight train between plots loads from.</summary>
    public static bool IsLoadingChest(string id) => id is LoadingChest or LoadingChest2 or LoadingChest3 or LoadingChest4;

    /// <summary>The chests a freight train between plots unloads into.</summary>
    public static bool IsReceivingChest(string id) => id is ReceivingChest or ReceivingChest2 or ReceivingChest3 or ReceivingChest4;

    public static bool IsClothing(string id) => Clothes.ContainsKey(id);

    /// <summary>Where each piece of clothing is worn and what it gives, in percent per industry (see <see cref="Industries"/>).</summary>
    public static readonly IReadOnlyDictionary<string, Garment> Clothes = new Dictionary<string, Garment>
    {
        [MinerHelmet] = new(ClothingSlots.Head, new Dictionary<string, int> { [Industries.Mining] = 15 }),
        [SouWester] = new(ClothingSlots.Head, new Dictionary<string, int> { [Industries.Fishing] = 15 }),
        [StrawHat] = new(ClothingSlots.Head, new Dictionary<string, int> { [Industries.Farming] = 15 }),
        [Beanie] = new(ClothingSlots.Head, new Dictionary<string, int> { [Industries.Forestry] = 10 }),
        [FlannelJacket] = new(ClothingSlots.Body, new Dictionary<string, int> { [Industries.Forestry] = 20 }),
        [SmithApron] = new(ClothingSlots.Body, new Dictionary<string, int> { [Industries.Crafting] = 20 }),
        [Oilskin] = new(ClothingSlots.Body, new Dictionary<string, int> { [Industries.Fishing] = 20 }),
        [SuitJacket] = new(ClothingSlots.Body, new Dictionary<string, int> { [Industries.Trade] = 4 }),
        [Overalls] = new(ClothingSlots.Legs, new Dictionary<string, int> { [Industries.Farming] = 20 }),
        [WorkTrousers] = new(ClothingSlots.Legs, new Dictionary<string, int> { [Industries.Mining] = 15, [Industries.Crafting] = 10 }),
        [SuitTrousers] = new(ClothingSlots.Legs, new Dictionary<string, int> { [Industries.Trade] = 3 }),
        [RubberBoots] = new(ClothingSlots.Feet, new Dictionary<string, int> { [Industries.Fishing] = 10, [Industries.Farming] = 10 }),
        [WorkBoots] = new(ClothingSlots.Feet, new Dictionary<string, int> { [Industries.Forestry] = 10, [Industries.Mining] = 10 }),
        [DressShoes] = new(ClothingSlots.Feet, new Dictionary<string, int> { [Industries.Trade] = 3 }),
        [LeatherBoots] = new(ClothingSlots.Feet, new Dictionary<string, int> { [Industries.Forestry] = 15, [Industries.Mining] = 15, [Industries.Farming] = 10 }),
    };

    /// <summary>What the character can eat and what each meal gives for a while, in percent per industry (see <see cref="Industries"/>),
    /// on top of the clothes. A new meal takes the place of the one before.</summary>
    public static readonly IReadOnlyDictionary<string, Meal> Meals = new Dictionary<string, Meal>
    {
        [Bread] = new(Industries.HandWork.ToDictionary(i => i, _ => 10), TimeSpan.FromMinutes(10)),
        [ScrambledEggs] = new(Industries.HandWork.ToDictionary(i => i, _ => 15), TimeSpan.FromMinutes(15)),
        [WorkersLunch] = new(Industries.HandWork.ToDictionary(i => i, _ => 15), TimeSpan.FromMinutes(20)),
        [CheeseSandwich] = new(Industries.HandWork.ToDictionary(i => i, _ => 20), TimeSpan.FromMinutes(20)),
        [MinersBreakfast] = new(new Dictionary<string, int> { [Industries.Mining] = 30 }, TimeSpan.FromMinutes(30)),
        [LumberjackGoulash] = new(new Dictionary<string, int> { [Industries.Forestry] = 30 }, TimeSpan.FromMinutes(30)),
    };

    /// <summary>City offices that sell a service to any player who comes by, for their owner.</summary>
    public static bool IsService(string id) => id is DesignOffice or Laboratory or ServiceWorkshop;

    /// <summary>The saplings of each kind of tree; the plain sapling grows a pine.</summary>
    public static bool IsSapling(string id) => id is Sapling or SaplingSpruce or SaplingBirch or SaplingOak or SaplingLinden;

    /// <summary>What the forest certificate makes dearer at the trader.</summary>
    public static bool IsTimber(string id) => id is Wood or OakWood or Planks or Beams;

    /// <summary>Coal and charcoal, which furnaces burn alike.</summary>
    public static bool IsCoal(string id) => id is Coal or Charcoal;

    /// <summary>Whether <paramref name="item"/> goes into a machine where a recipe asks for <paramref name="ingredient"/>:
    /// the item itself, or charcoal for coal, so steel and silicon can be made of wood.</summary>
    public static bool Fills(string ingredient, string item) => item == ingredient || (ingredient == Coal && item == Charcoal);

    /// <summary>What else than itself goes into a machine for <paramref name="ingredient"/>.</summary>
    public static IReadOnlyList<string> StandIns(string ingredient) => ingredient == Coal ? [Charcoal] : [];

    /// <summary>Things an expedition brings back for the album; the trader neither buys nor sells them.</summary>
    public static bool IsSouvenir(string id) => id.StartsWith("souvenir_", StringComparison.Ordinal);

    /// <summary>Decorations that only stand on a plot and look nice.</summary>
    public static bool IsDecoration(string id) => id.StartsWith("decor_", StringComparison.Ordinal);

    /// <summary>Fossils the dunes give up now and then, a collection of three.</summary>
    public static bool IsFossil(string id) => id is Ammonite or AncientCoin or DinosaurBone;
}

/// <param name="ExtraSlots">Inventory tiles the backpack adds.</param>
/// <param name="ToolSlots">Tool bar slots in all while it is worn.</param>
public sealed record Backpack(int ExtraSlots, int ToolSlots);

/// <param name="Time">What part of an axe's time the saw takes to cut a tree.</param>
/// <param name="ExtraWood">Wood a tree gives over what it gives an axe.</param>
/// <param name="Fells">Grown trees next to the one cut that fall at the same cut.</param>
/// <param name="MoreSaplings">Whether saplings fall twice as often.</param>
/// <param name="ThroughBeetle">Whether a tree the bark beetle is in still gives all its wood.</param>
public sealed record Saw(double Time, int ExtraWood, int Fells, bool MoreSaplings, bool ThroughBeetle);

/// <param name="Slot">One of <see cref="ClothingSlots.All"/>.</param>
/// <param name="Bonuses">Percent per industry: work in it goes this much faster (trade: the trader pays this much more).</param>
public sealed record Garment(string Slot, IReadOnlyDictionary<string, int> Bonuses);

/// <param name="Bonuses">Percent per industry: work in it goes this much faster while the meal lasts.</param>
/// <param name="Time">How long it lasts.</param>
public sealed record Meal(IReadOnlyDictionary<string, int> Bonuses, TimeSpan Time);

/// <summary>Where clothes are worn.</summary>
public static class ClothingSlots
{
    public const string Head = "head";
    public const string Body = "body";
    public const string Legs = "legs";
    public const string Feet = "feet";

    public static readonly IReadOnlyList<string> All = [Head, Body, Legs, Feet];
}

/// <summary>The branches of work clothes help in.</summary>
public static class Industries
{
    /// <summary>Cutting trees, pulling out bushes and picking mushrooms by hand.</summary>
    public const string Forestry = "forestry";

    /// <summary>Breaking rocks and ice, digging snow and working mines with a pickaxe.</summary>
    public const string Mining = "mining";

    /// <summary>Tilling, sowing and reaping with a hoe.</summary>
    public const string Farming = "farming";

    /// <summary>Fishing with a rod.</summary>
    public const string Fishing = "fishing";

    /// <summary>Orders at the workbench, the anvil, the shipyard and the tailor's.</summary>
    public const string Crafting = "crafting";

    /// <summary>Selling to the trader.</summary>
    public const string Trade = "trade";

    public static readonly IReadOnlyList<string> All = [Forestry, Mining, Farming, Fishing, Crafting, Trade];

    /// <summary>Work done by hand, which meals make faster: everything but trade.</summary>
    public static readonly IReadOnlyList<string> HandWork = [Forestry, Mining, Farming, Fishing, Crafting];

    /// <summary>How many times faster work goes with <paramref name="percent"/> from clothes.</summary>
    public static double Speed(int percent) => 1 + percent / 100.0;
}
