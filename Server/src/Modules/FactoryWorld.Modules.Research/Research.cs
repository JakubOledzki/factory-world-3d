using System.Text.Json;
using FactoryWorld.Modules.Abstractions;
using FactoryWorld.Modules.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryWorld.Modules.Research;

/// <summary>One node of the technology tree.</summary>
/// <param name="Id">Stored in the database, so never rename one.</param>
/// <param name="Requires">Technologies that must be researched first.</param>
/// <param name="Cost">Items used up when the research starts.</param>
/// <param name="Unlocks">Items (workbench recipes, machines, machine recipes) the player may make and buy once it is done.</param>
/// <param name="CompanyOnly">Only a company researches it, for all its members (<see cref="CompanyTechs"/>).</param>
/// <param name="Icon">The item whose icon it shows when it unlocks none (e.g. <see cref="GripperTechs"/>).</param>
/// <param name="CostCents">Money used up besides the items, in cents; a company collects it as <see cref="TechTree.Money"/>.</param>
public sealed record Technology(
    string Id, string Name, string Description, IReadOnlyList<string> Requires,
    IReadOnlyList<(string Item, int Quantity)> Cost, TimeSpan Time, int Xp, IReadOnlyList<string> Unlocks, bool CompanyOnly = false,
    string? Icon = null, long CostCents = 0);

/// <summary>
/// The technology tree. A new player can make the workbench, the stone furnace and nails, and
/// everything else that no technology lists; belts, inserters, storage and the food machines
/// are researched first. Research uses up its cost at once and takes time, one technology at a
/// time, and goes on while the player is away. Trading between players is never locked, so a
/// player may buy from others what they have not researched yet.
/// </summary>
public static class TechTree
{
    public const string Logistics = "logistics";
    public const string Automation = "automation";
    public const string Sorting = "sorting";
    public const string FastLogistics = "fast_logistics";
    public const string ExpressLogistics = "express_logistics";
    public const string UndergroundBelts = "underground_belts";
    public const string LongInserters = "long_inserters";
    public const string LogisticsDepot = "logistics_depot";
    public const string BiggerGrippers = GripperTechs.Bigger;
    public const string IndustrialGrippers = GripperTechs.Industrial;
    public const string ExpressCable = "express_cable";
    public const string Drones = "drones";
    public const string Storage = "storage";
    public const string Milling = "milling";
    public const string RollerMilling = "roller_milling";
    public const string FlourIndustry = "flour_industry";
    public const string BrickFurnaces = "brick_furnaces";
    public const string BlastFurnaces = "blast_furnaces";
    public const string MechanizedQuarrying = "mechanized_quarrying";
    public const string OpenPitMining = "open_pit_mining";
    public const string ConeCrushing = "cone_crushing";
    public const string OreProcessing = "ore_processing";
    public const string OilPressing = "oil_pressing";
    public const string Pickling = "pickling";
    public const string Baking = "baking";
    public const string GrainStorage = "grain_storage";
    public const string Cooking = "cooking";
    public const string Confectionery = "confectionery";
    public const string ForestryIndustry = "forestry_industry";
    public const string Woodworking = "woodworking";
    public const string IndustrialForestry = "industrial_forestry";
    public const string Sawing = "sawing";
    public const string Harvesters = "harvesters";
    public const string MeteoriteSaws = "meteorite_saws";
    public const string MiningAutomation = "mining_automation";
    public const string Crushing = "crushing";
    public const string Stonemasonry = "stonemasonry";
    public const string Hydropower = "hydropower";
    public const string Metallurgy = "metallurgy";
    public const string Explosives = "explosives";
    public const string DeepMining = "deep_mining";
    public const string MeteoriteTools = "meteorite_tools";
    public const string HydroPlant = "hydro_plant";
    public const string CableTransport = "cable_transport";
    public const string Fishing = "fishing";
    public const string FishProcessing = "fish_processing";
    public const string Seafaring = "seafaring";
    public const string Aquaculture = "aquaculture";
    public const string SaltWorks = "salt_works";
    public const string TidalPower = "tidal_power";
    public const string OilExtraction = "oil_extraction";
    public const string Refining = "refining";
    public const string Cracking = "cracking";
    public const string Petrochemistry = "petrochemistry";
    public const string Combustion = "combustion";
    public const string Roads = "roads";
    public const string OffshoreDrilling = "offshore_drilling";
    public const string EnhancedRecovery = "enhanced_recovery";
    public const string Glassmaking = "glassmaking";
    public const string SiliconWorks = "silicon";
    public const string SolarPower = "solar_power";
    public const string SolarTower = "solar_tower";
    public const string WaterSupply = "water_supply";
    public const string BuildingMaterials = "building_materials";
    public const string WindPower = "wind_power";
    public const string LargeWind = "large_wind";
    public const string Heating = "heating";
    public const string Refrigeration = "refrigeration";
    public const string Glaciology = "glaciology";
    public const string DataCenter = "data_center";
    public const string PolarTourism = "polar_tourism";
    public const string PolarTransport = "polar_transport";
    public const string Assembly = "assembly";
    public const string Appliances = "appliances";
    public const string Commerce = "commerce";
    public const string Assembling = "assembling";
    public const string FastAssembling = "fast_assembling";
    public const string ExpressAssembling = "express_assembling";
    public const string UrbanConstruction = "urban_construction";
    public const string CityServices = "city_services";
    public const string WasteManagement = "waste_management";
    public const string FreightHub = "freight_hub";
    public const string ExportMarket = "export_market";
    public const string RailFerry = "rail_ferry";
    public const string RailFreight = "rail_freight";
    public const string RailFreight2 = "rail_freight_2";
    public const string RailFreight3 = "rail_freight_3";
    public const string RailFreight4 = "rail_freight_4";
    public const string Headquarters = "headquarters";
    public const string Tailoring = "tailoring";
    public const string Silviculture = "silviculture";
    public const string Papermaking = "papermaking";
    public const string Furniture = "furniture";
    public const string Charring = "charring";
    public const string Beekeeping = "beekeeping";
    public const string ForestFoods = "forest_foods";
    public const string FireProtection = "fire_protection";
    public const string Breeding = "breeding";
    public const string Dairying = "dairying";
    public const string Butchery = "butchery";
    public const string Biogas = "biogas";
    public const string Distilling = "distilling";
    public const string Horsemanship = "horsemanship";
    public const string WorksCanteens = "works_canteens";
    public const string WorkMeals = "work_meals";
    public const string FineCuisine = "fine_cuisine";
    public const string BioLab = "bio_lab";
    public const string Farmsteads = "farmsteads";
    public const string ManorFarms = "manor_farms";
    public const string Feeders = "feeders";
    public const string Geology = GeologyTech.Id;
    public const string Coking = "coking";
    public const string PreciousMetals = "precious_metals";
    public const string TreeNursery = "tree_nursery";
    public const string Textiles = "textiles";
    public const string AutoBarns = "auto_barns";
    public const string PigsAndGoats = "pigs_and_goats";
    public const string Irrigation = "irrigation";
    public const string MechanizedFarming = "mechanized_farming";
    public const string SugarBeets = "sugar_beets";
    public const string FiberCrops = "fiber_crops";
    public const string BiomassPower = "biomass_power";
    public const string Cogeneration = "cogeneration";
    public const string GeothermalPower = "geothermal_power";
    public const string HydrogenPower = "hydrogen_power";

    // The crafting tree: one technology for each of its items, named after it.
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
    public const string MachineShop = "machine_shop";
    public const string RoboticHall = "robotic_hall";
    public const string OxygenConverter = "oxygen_converter";
    public const string RollingMill = "rolling_mill";
    public const string ControlCenter = "control_center";
    public const string ContinuousCaster = "continuous_caster";
    public const string AutomatedWarehouse = "automated_warehouse";
    public const string SteelCombine = "steel_combine";
    public const string Megafactory = "megafactory";
    public const string FusionPlant = "fusion_plant";
    public const string PowerDistribution = "power_distribution";
    public const string ThermalPower = "thermal_power";
    public const string EmissionControl = "emission_control";
    public const string EnergyStorage = "energy_storage";
    public const string NuclearPower = "nuclear_power";

    public const string Megaprojects = CompanyTechs.Megaprojects;
    public const string CorporateFinance = CompanyTechs.CorporateFinance;
    public const string Banking = CompanyTechs.Banking;

    /// <summary>What members bring as money towards a company's research, in cents, in place of an item.</summary>
    public const string Money = "money";

    public static readonly IReadOnlyList<Technology> Technologies =
    [
        new(Logistics, "Logistyka", "Taśmociągi przewożą przedmioty po działce, a skrzynie i stosy drewna je przechowują.",
            [], [(Items.IronIngot, 10), (Items.Wood, 20)], TimeSpan.FromSeconds(60), 20,
            [Items.Belt, Items.WoodenChest, Items.WoodPile]),
        new(Milling, "Młynarstwo", "Młyn miele pszenicę i kukurydzę na mąkę.",
            [], [(Items.Wheat, 40), (Items.Stone, 20)], TimeSpan.FromSeconds(60), 20,
            [Items.Mill]),
        new(OilPressing, "Tłoczenie oleju", "Tłocznia wyciska olej ze słonecznika.",
            [], [(Items.Sunflower, 30), (Items.IronIngot, 5)], TimeSpan.FromSeconds(90), 20,
            [Items.OilPress]),
        new(Pickling, "Kiszenie", "Beczka do kiszenia zamienia kapustę w kapustę kiszoną.",
            [], [(Items.Cabbage, 20), (Items.Wood, 20), (Items.Nails, 10)], TimeSpan.FromSeconds(60), 20,
            [Items.PicklingBarrel]),
        new(Automation, "Automatyzacja", "Podajniki same przekładają przedmioty między taśmami, maszynami i magazynami.",
            [Logistics], [(Items.IronIngot, 20), (Items.CopperIngot, 10), (Items.Nails, 20)], TimeSpan.FromMinutes(2), 40,
            [Items.Inserter]),
        new(Storage, "Magazynowanie", "Skrzynia żelazna mieści trzy razy więcej niż drewniana, a hałda 5000 sztuk jednego surowca.",
            [Logistics], [(Items.IronIngot, 20), (Items.Stone, 50)], TimeSpan.FromSeconds(90), 30,
            [Items.IronChest, Items.Heap]),
        new(Baking, "Piekarnictwo", "Piec piekarski wypieka chleb z mąki.",
            [Milling], [(Items.Flour, 20), (Items.Stone, 40), (Items.Sand, 10)], TimeSpan.FromMinutes(2), 40,
            [Items.Bakery]),
        new(RollerMilling, "Młyny walcowe", "Młyn walcowy (3 × 3 pola) na prąd miele 3 razy szybciej niż młyn i robi o ćwierć więcej mąki z tego samego zboża.",
            [Milling, Automation], [(Items.Flour, 40), (Items.IronIngot, 30), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.RollerMill]),
        new(FlourIndustry, "Przemysł młyński", "Zakłady młyńskie (4 × 4 pola) na prąd mielą 6 razy szybciej niż młyn i robią o połowę więcej mąki z tego samego zboża.",
            [RollerMilling, Metallurgy], [(Items.Flour, 100), (Items.SteelIngot, 20), (Items.Bricks, 30)], TimeSpan.FromMinutes(5), 100,
            [Items.FlourWorks]),
        new(Sorting, "Sortowanie", "Rozdzielacz dzieli taśmę na dwie i odsyła wybrane przedmioty na jedną stronę.",
            [Automation], [(Items.Belt, 10), (Items.Inserter, 4), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.Splitter]),
        new(FastLogistics, "Szybka logistyka", "Szybki taśmociąg wozi przedmioty 2 razy szybciej, a szybki podajnik na prąd obraca ramię 2 razy szybciej.",
            [Automation], [(Items.Belt, 20), (Items.Inserter, 5), (Items.IronIngot, 50), (Items.CopperIngot, 30)], TimeSpan.FromMinutes(4), 80,
            [Items.FastBelt, Items.FastInserter]),
        new(ExpressLogistics, "Ekspresowa logistyka", "Ekspresowy taśmociąg wozi przedmioty 3 razy szybciej, a ekspresowy podajnik obraca ramię 4 razy szybciej.",
            [FastLogistics, Metallurgy], [(Items.FastBelt, 20), (Items.FastInserter, 5), (Items.SteelIngot, 30), (Items.TinIngot, 20), (Items.SilverIngot, 10)], TimeSpan.FromMinutes(6), 120,
            [Items.ExpressBelt, Items.ExpressInserter]),
        new(Assembling, "Stacje montażowe", "Stacja montażowa (2 × 2 pola) sama robi przepisy stołu warsztatowego z tego, co włożą do niej podajniki, bez prądu. Stoi tylko na działce w mieście.",
            [Automation], [(Items.Inserter, 4), (Items.IronIngot, 30), (Items.Nails, 20)], TimeSpan.FromMinutes(2), 50,
            [Items.Assembler]),
        new(FastAssembling, "Szybki montaż", "Szybka stacja montażowa (3 × 3 pola) na prąd robi przepisy stołu warsztatowego 2 razy szybciej; tylko w mieście.",
            [Assembling, FastLogistics, Metallurgy], [(Items.Assembler, 2), (Items.FastInserter, 4), (Items.SteelIngot, 30)], TimeSpan.FromMinutes(5), 100,
            [Items.FastAssembler]),
        new(ExpressAssembling, "Ekspresowy montaż", "Ekspresowa stacja montażowa (4 × 4 pola) na prąd robi przepisy stołu warsztatowego 4 razy szybciej; tylko w mieście.",
            [FastAssembling, ExpressLogistics, Assembly], [(Items.FastAssembler, 2), (Items.ExpressInserter, 4), (Items.ElectricMotor, 10), (Items.CircuitBoard, 10)],
            TimeSpan.FromMinutes(8), 160,
            [Items.ExpressAssembler]),
        new(UndergroundBelts, "Taśmociągi podziemne", "Taśmociąg podziemny chowa przedmioty pod ziemię i wynosi je na następnym takim samym przed nim, do 4 pól dalej, więc linie mogą się krzyżować.",
            [Automation], [(Items.Belt, 20), (Items.IronIngot, 40), (Items.Stone, 50)], TimeSpan.FromMinutes(3), 60,
            [Items.UndergroundBelt]),
        new(LongInserters, "Długoramienne podajniki", "Długoramienny podajnik bierze przedmioty 2 pola za sobą i odkłada je 2 pola przed sobą, ponad tym, co stoi obok.",
            [FastLogistics], [(Items.FastInserter, 4), (Items.IronIngot, 40), (Items.CopperIngot, 30)], TimeSpan.FromMinutes(4), 80,
            [Items.LongInserter]),
        new(BiggerGrippers, "Większe chwytaki", "Twoje podajniki biorą naraz po 2 sztuki tego samego przedmiotu.",
            [FastLogistics], [(Items.Inserter, 10), (Items.IronIngot, 60), (Items.CopperIngot, 40)], TimeSpan.FromMinutes(5), 100,
            [], Icon: Items.FastInserter),
        new(IndustrialGrippers, "Chwytaki przemysłowe", "Twoje podajniki biorą naraz po 3 sztuki tego samego przedmiotu.",
            [BiggerGrippers, ExpressLogistics], [(Items.FastInserter, 10), (Items.SteelIngot, 40), (Items.SilverIngot, 10)], TimeSpan.FromMinutes(8), 150,
            [], Icon: Items.ExpressInserter),
        new(LogisticsDepot, "Magazyn logistyczny", "Magazyn logistyczny ma 48 miejsc po 200 sztuk: duży bufor, z którego podajniki biorą i do którego odkładają.",
            [Storage, Metallurgy], [(Items.IronChest, 4), (Items.SteelIngot, 30), (Items.Beams, 20)], TimeSpan.FromMinutes(4), 80,
            [Items.LogisticsDepot]),
        new(ExpressCable, "Ekspresowa kolejka linowa", "Ekspresowa stacja kolejki linowej wiezie przedmioty 1,5 raza szybciej i do 48 pól dalej.",
            [CableTransport, ExpressLogistics], [(Items.CableStation, 2), (Items.SteelIngot, 40), (Items.CopperIngot, 40)], TimeSpan.FromMinutes(6), 120,
            [Items.ExpressCableStation]),
        new(Drones, "Drony transportowe", "Drony przenoszą przedmioty z portów bez filtra do portów, które mają ustawiony filtr na dany przedmiot, po całej działce, bez taśm.",
            [ExpressLogistics, LogisticsDepot, Assembly], [(Items.CircuitBoard, 20), (Items.ElectricMotor, 6), (Items.SteelIngot, 60)], TimeSpan.FromMinutes(10), 200,
            [Items.DronePort]),
        new(RailFreight, "Transport kolejowy", "Skrzynia ładująca i odbierająca (800 sztuk): pociąg trasy między twoimi działkami czeka, aż skrzynie ładujące jednej działki będą miały cały towar, i zawozi go do skrzyń odbierających drugiej.",
            [Storage, Automation], [(Items.IronChest, 2), (Items.IronIngot, 40), (Items.Nails, 30)], TimeSpan.FromMinutes(2), 50,
            [Items.LoadingChest, Items.ReceivingChest]),
        new(RailFreight2, "Większe skrzynie kolejowe", "Skrzynia ładująca i odbierająca II mieszczą po 1600 sztuk.",
            [RailFreight, FastLogistics], [(Items.LoadingChest, 2), (Items.ReceivingChest, 2), (Items.IronIngot, 60)], TimeSpan.FromMinutes(4), 80,
            [Items.LoadingChest2, Items.ReceivingChest2]),
        new(RailFreight3, "Terminal przeładunkowy", "Skrzynia ładująca i odbierająca III mieszczą po 6400 sztuk.",
            [RailFreight2, LogisticsDepot], [(Items.LoadingChest2, 2), (Items.ReceivingChest2, 2), (Items.SteelIngot, 40), (Items.Beams, 30)], TimeSpan.FromMinutes(6), 120,
            [Items.LoadingChest3, Items.ReceivingChest3]),
        new(RailFreight4, "Kontenerowy terminal kolejowy", "Skrzynia ładująca i odbierająca IV mieszczą po 16 000 sztuk.",
            [RailFreight3, ExpressLogistics, Assembly], [(Items.LoadingChest3, 2), (Items.ReceivingChest3, 2), (Items.SteelIngot, 80), (Items.ElectricMotor, 8)],
            TimeSpan.FromMinutes(10), 200,
            [Items.LoadingChest4, Items.ReceivingChest4]),
        new(GrainStorage, "Silosy zbożowe", "Silos mieści 2000 pszenicy, kukurydzy albo słonecznika.",
            [Storage, Milling], [(Items.Wheat, 100), (Items.IronIngot, 20)], TimeSpan.FromMinutes(2), 40,
            [Items.GrainSilo]),
        new(Cooking, "Kuchnia polowa", "Kuchnia gotuje frytki, zupę jarzynową i bigos.",
            [OilPressing, Pickling], [(Items.Sauerkraut, 20), (Items.SunflowerOil, 10), (Items.Potato, 30)], TimeSpan.FromSeconds(150), 60,
            [Items.Kitchen]),
        new(Confectionery, "Cukiernictwo", "Piec piekarski piecze też ciasto marchewkowe.",
            [Baking, OilPressing], [(Items.Bread, 10), (Items.SunflowerOil, 10), (Items.Carrot, 30)], TimeSpan.FromMinutes(3), 60,
            [Items.CarrotCake]),
        new(ForestryIndustry, "Gospodarka leśna", "Chata leśnika sama sadzi i ścina las wokół siebie, a tartak tnie drewno na deski, belki i pellet.",
            [], [(Items.Sapling, 20), (Items.Wood, 50)], TimeSpan.FromSeconds(90), 30,
            [Items.ForesterHut, Items.Sawmill, Items.Planks, Items.Beams, Items.WoodPellets]),
        new(Woodworking, "Przemysł drzewny", "Leśniczówka ma większy las, który rośnie szybciej; tartak elektryczny tnie dwa razy szybciej i skleja sklejkę z desek.",
            [ForestryIndustry, Automation], [(Items.Planks, 40), (Items.Beams, 20), (Items.IronIngot, 20)], TimeSpan.FromMinutes(3), 60,
            [Items.ForesterLodge, Items.ElectricSawmill, Items.Plywood]),
        new(IndustrialForestry, "Leśnictwo przemysłowe", "Nadleśnictwo prowadzi największy las, a tartak przemysłowy robi z kłody trzy deski.",
            [Woodworking], [(Items.Plywood, 20), (Items.Beams, 40), (Items.CopperIngot, 20)], TimeSpan.FromMinutes(5), 100,
            [Items.ForestryStation, Items.IndustrialSawmill]),
        new(Sawing, "Pilarstwo", "Piłę stalową wykuwasz na kowadle: ścina drzewo w 1 s i daje z niego o 1 drewno więcej.",
            [Woodworking, Metallurgy], [(Items.Wood, 80), (Items.SteelIngot, 15), (Items.BronzeIngot, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.SteelSaw]),
        new(Harvesters, "Harwestery", "Piła harwesterowa ścina drzewo w 1 s razem z 2 sąsiednimi, daje o 1 drewno więcej z każdego, a sadzonki wypadają z niej 2 razy częściej.",
            [Sawing, IndustrialForestry], [(Items.Plywood, 30), (Items.SteelIngot, 25), (Items.CopperIngot, 20)], TimeSpan.FromMinutes(5), 100,
            [Items.HarvesterSaw]),
        new(MeteoriteSaws, "Piła z meteorytu", "Piła meteorytowa ścina drzewo w 0,5 s razem z 4 sąsiednimi, daje o 2 drewna więcej z każdego i całe drewno nawet z drzewa z kornikiem.",
            [Harvesters, MeteoriteTools], [(Items.MeteoricIron, 10), (Items.Iridium, 1), (Items.Plywood, 20)], TimeSpan.FromMinutes(6), 120,
            [Items.MeteoriteSaw]),
        new(Stonemasonry, "Kamieniarstwo", "Warsztat kamieniarski ciosa z kamienia bloki, a z marmuru płyty.",
            [], [(Items.Stone, 100), (Items.IronIngot, 10)], TimeSpan.FromSeconds(90), 30,
            [Items.Stonemason]),
        new(Hydropower, "Energia wodna", "Koło wodne nad wodą albo w górach daje prąd bez paliwa.",
            [], [(Items.Wood, 40), (Items.IronIngot, 20), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(2), 40,
            [Items.WaterWheel]),
        new(MiningAutomation, "Automatyzacja górnictwa", "Wiertło przy kopalni samo wydobywa rudę, a kamieniołom w górach łamie kamień.",
            [Automation], [(Items.IronIngot, 40), (Items.CopperIngot, 20), (Items.Inserter, 4)], TimeSpan.FromMinutes(3), 60,
            [Items.Drill, Items.Quarry]),
        new(Metallurgy, "Metalurgia", "Odlewnia stapia brąz i stal i wypala cegły; z brązu wykujesz lepszy kilof.",
            [], [(Items.CopperIngot, 30), (Items.TinIngot, 10), (Items.IronIngot, 30), (Items.Coal, 30)], TimeSpan.FromMinutes(3), 60,
            [Items.Foundry, Items.BronzePickaxe]),
        new(BrickFurnaces, "Piece ceglane", "Piec ceglany (3 × 3 pola) przetapia rudę 2 razy szybciej niż kamienny i mieści po 100 rudy i węgla.",
            [], [(Items.Stone, 60), (Items.Sand, 30), (Items.IronIngot, 15)], TimeSpan.FromMinutes(2), 40,
            [Items.BrickFurnace]),
        new(BlastFurnaces, "Piece hutnicze", "Piec hutniczy (4 × 4 pola) przetapia rudę 4 razy szybciej niż kamienny, mieści po 200 rudy i węgla, a węgiel starcza mu na 8 przetopień.",
            [BrickFurnaces, Metallurgy], [(Items.Bricks, 40), (Items.SteelIngot, 20), (Items.Coal, 40)], TimeSpan.FromMinutes(4), 80,
            [Items.BlastFurnace]),
        new(MechanizedQuarrying, "Kamieniołom zmechanizowany", "Zmechanizowany kamieniołom (3 × 3 pola) na prąd łamie 3 razy więcej kamienia niż zwykły.",
            [MiningAutomation, Metallurgy], [(Items.Stone, 200), (Items.SteelIngot, 15), (Items.CopperIngot, 20)], TimeSpan.FromMinutes(4), 80,
            [Items.MechanizedQuarry]),
        new(OpenPitMining, "Górnictwo odkrywkowe", "Kopalnia odkrywkowa (4 × 4 pola) na prąd łamie 6 razy więcej kamienia niż kamieniołom.",
            [MechanizedQuarrying, Explosives], [(Items.Stone, 400), (Items.Dynamite, 20), (Items.SteelIngot, 30)], TimeSpan.FromMinutes(6), 120,
            [Items.OpenPitMine]),
        new(Crushing, "Kruszenie", "Kruszarka mieli kamień na piasek, a rudę na kruszywo, z którego wytopisz więcej sztabek.",
            [MiningAutomation], [(Items.Stone, 100), (Items.IronIngot, 30)], TimeSpan.FromMinutes(3), 60,
            [Items.Crusher]),
        new(ConeCrushing, "Kruszarki stożkowe", "Kruszarka stożkowa (3 × 3 pola) kruszy 3 razy szybciej niż kruszarka, a z 4 rud robi 7 kruszywa zamiast 6.",
            [Crushing, Metallurgy], [(Items.Sand, 100), (Items.SteelIngot, 15), (Items.IronIngot, 30)], TimeSpan.FromMinutes(4), 80,
            [Items.ConeCrusher]),
        new(OreProcessing, "Przeróbka rud", "Zakład przeróbczy (4 × 4 pola) kruszy 6 razy szybciej niż kruszarka, a z każdej rudy robi 2 kruszywa.",
            [ConeCrushing, DeepMining], [(Items.CrushedIronOre, 100), (Items.SteelIngot, 30), (Items.BronzeIngot, 20)], TimeSpan.FromMinutes(6), 120,
            [Items.OreProcessingPlant]),
        new(Explosives, "Materiały wybuchowe", "Dynamit wydobywa z kopalni pięć urobków naraz.",
            [Metallurgy], [(Items.Sulfur, 20), (Items.Coal, 20)], TimeSpan.FromMinutes(2), 50,
            [Items.Dynamite]),
        new(DeepMining, "Głębokie górnictwo", "Obudowa górnicza pozwala pogłębić kopalnię do poziomu 4 i 5; wiertło udarowe i kilof stalowy kopią szybciej.",
            [MiningAutomation, Metallurgy], [(Items.SteelIngot, 30), (Items.Beams, 20), (Items.BronzeIngot, 20)], TimeSpan.FromMinutes(5), 100,
            [Items.MineSupport, Items.HeavyDrill, Items.SteelPickaxe]),
        new(MeteoriteTools, "Narzędzia z meteorytu", "Żelazo meteorytowe z lodowca i iryd wykuwasz na kowadle w kilof, który kopie dwa razy szybciej niż żelazny; dopiero wtedy sprzeda go też handlarz.",
            [DeepMining], [(Items.MeteoricIron, 10), (Items.SteelIngot, 20)], TimeSpan.FromMinutes(4), 80,
            [Items.MeteoritePickaxe]),
        new(HydroPlant, "Elektrownia wodna", "Mała elektrownia wodna daje pięć razy więcej prądu niż koło wodne.",
            [Hydropower, Metallurgy], [(Items.SteelIngot, 20), (Items.Bricks, 40), (Items.CopperIngot, 30)], TimeSpan.FromMinutes(5), 100,
            [Items.HydroPlant]),
        new(CableTransport, "Kolejka linowa", "Stacje kolejki linowej wożą przedmioty po linie nad wszystkim, co stoi po drodze, do 24 pól.",
            [Logistics, Metallurgy], [(Items.SteelIngot, 10), (Items.CopperIngot, 20), (Items.Belt, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.CableStation]),
        new(Fishing, "Rybołówstwo", "Stocznia buduje kutry i łodzie, które same łowią ryby z brzegu, a wędzarnia wędzi dorsza.",
            [], [(Items.Herring, 20), (Items.Wood, 40), (Items.IronIngot, 10)], TimeSpan.FromSeconds(90), 30,
            [Items.Shipyard, Items.FishingBoat, Items.RowingBoat, Items.Smokehouse, Items.SmokedFish]),
        new(FishProcessing, "Przetwórstwo ryb", "Konserwiarnia puszkuje śledzie i mieli resztki na mączkę i olej; śledzie się soli, karpia smaży, a z szczupaka gotuje zupę.",
            [Fishing, OilPressing], [(Items.Herring, 40), (Items.TinIngot, 5), (Items.SunflowerOil, 5)], TimeSpan.FromMinutes(2), 50,
            [Items.Cannery, Items.CannedFish, Items.FishMeal, Items.FishOil, Items.SaltedHerring, Items.FriedCarp, Items.FishSoup]),
        new(Seafaring, "Żegluga", "Przystań wysyła towary statkiem do przystani innej działki, a falochron pozwala łowić w sztorm; latarnia skraca rejsy.",
            [Fishing, Logistics], [(Items.Planks, 40), (Items.Beams, 20), (Items.Stone, 60)], TimeSpan.FromMinutes(3), 60,
            [Items.Port, Items.Breakwater, Items.Lighthouse]),
        new(Aquaculture, "Akwakultura", "Farma ryb hoduje łososie na mączce rybnej i małże na linach; wędzarnia wędzi też łososia.",
            [FishProcessing], [(Items.FishMeal, 20), (Items.Beams, 10), (Items.IronIngot, 20)], TimeSpan.FromMinutes(3), 60,
            [Items.FishFarm, Items.SmokedSalmon]),
        new(SaltWorks, "Warzelnictwo", "Warzelnia na brzegu morza odparowuje sól z wody morskiej.",
            [], [(Items.Stone, 40), (Items.Sand, 20)], TimeSpan.FromSeconds(60), 20,
            [Items.SaltPan]),
        new(TidalPower, "Energia pływów", "Elektrownia pływowa na brzegu morza daje prąd bez paliwa, dzień i noc.",
            [Hydropower, Seafaring], [(Items.SteelIngot, 20), (Items.CopperIngot, 30), (Items.Bricks, 40)], TimeSpan.FromMinutes(5), 100,
            [Items.TidalPlant]),
        new(OilExtraction, "Wydobycie ropy", "Żuraw pompowy i głowica gazowa pompują to, co badanie znalazło pod działką; rury, pompy i zbiorniki przenoszą płyny.",
            [Metallurgy, Automation], [(Items.SteelIngot, 500), (Items.IronIngot, 1_500), (Items.CopperIngot, 1_000)], TimeSpan.FromMinutes(150), 80,
            [Items.Pumpjack, Items.GasWell, Items.Pipe, Items.Pump, Items.Tank, Items.Gasket]),
        new(Refining, "Rafinacja", "Destylarnia i rafineria rozdzielają ropę na benzynę, olej napędowy, naftę i asfalt.",
            [OilExtraction], [(Items.CrudeOil, 2_500), (Items.SteelIngot, 1_000), (Items.Bricks, 1_000)], TimeSpan.FromMinutes(200), 100,
            [Items.Distillery, Items.Refinery, Items.Gasoline, Items.Diesel, Items.Kerosene, Items.Asphalt]),
        new(Cracking, "Kraking", "Instalacja krakingu rozbija ciężkie paliwa na benzynę.",
            [Refining], [(Items.Diesel, 1_500), (Items.Kerosene, 1_500), (Items.SteelIngot, 1_000)], TimeSpan.FromMinutes(250), 120,
            [Items.Cracker]),
        new(Petrochemistry, "Petrochemia", "Zakład chemiczny robi z gazu tworzywo sztuczne i nawóz, a z nafty i siarki kauczuk na opony.",
            [Refining], [(Items.NaturalGas, 2_000), (Items.Kerosene, 1_000), (Items.SteelIngot, 1_000)], TimeSpan.FromMinutes(250), 120,
            [Items.ChemicalPlant, Items.Plastic, Items.SyntheticRubber, Items.Fertilizer]),
        new(Combustion, "Silniki spalinowe", "Elektrownia gazowa i agregat spalinowy dają dużo prądu z gazu, oleju napędowego albo benzyny.",
            [Refining], [(Items.Diesel, 1_000), (Items.SteelIngot, 1_000), (Items.CopperIngot, 1_000)], TimeSpan.FromMinutes(200), 100,
            [Items.GasPlant, Items.DieselGenerator]),
        new(Roads, "Drogi", "Z asfaltu i kamienia układa się drogę: po asfalcie chodzi się szybciej. Baza ciężarówek wozi towar na olej napędowy do stacji w pobliżu, bez opłaty za kolej.",
            [Refining], [(Items.Asphalt, 1_000), (Items.Stone, 2_000)], TimeSpan.FromMinutes(100), 40,
            [Items.Road, Items.TruckDepot]),
        new(OffshoreDrilling, "Wiercenia morskie", "Platforma wiertnicza na brzegu morza pompuje ropę spod dna, z dużego złoża.",
            [OilExtraction, Seafaring], [(Items.SteelIngot, 2_000), (Items.CrudeOil, 5_000), (Items.Beams, 1_000)], TimeSpan.FromHours(5), 150,
            [Items.OffshorePlatform]),
        new(EnhancedRecovery, "Wspomagane wydobycie", "Tłocznia wody wyciska ze złoża o połowę więcej ropy, a separator zbiera wycieki.",
            [OilExtraction], [(Items.CrudeOil, 4_000), (Items.SteelIngot, 1_000), (Items.Gasket, 500)], TimeSpan.FromMinutes(200), 100,
            [Items.WaterInjector, Items.OilSeparator]),
        new(Glassmaking, "Hutnictwo szkła", "Huta szkła topi piasek na szkło (na piasku o 30% szybciej), dmucha butelki ze stłuczki i robi szyby i lustra.",
            [], [(Items.Sand, 60), (Items.Stone, 20), (Items.Wood, 20)], TimeSpan.FromSeconds(90), 30,
            [Items.Glassworks, Items.Glass, Items.GlassBottles, Items.WindowPane, Items.Mirror]),
        new(SiliconWorks, "Krzem", "Piec łukowy na prąd robi krzem z kwarcu i tnie go na płytki, a huta szkła ciągnie światłowód.",
            [Glassmaking, Metallurgy], [(Items.Quartz, 20), (Items.Coal, 20), (Items.CopperIngot, 20)], TimeSpan.FromMinutes(3), 80,
            [Items.ArcFurnace, Items.Silicon, Items.SiliconWafer, Items.OpticalFiber]),
        new(SolarPower, "Energia słoneczna", "Panele słoneczne dają prąd za dnia, akumulatory go przechowują, a płot trzcinowy chroni przed burzą piaskową.",
            [SiliconWorks], [(Items.SiliconWafer, 10), (Items.Glass, 20), (Items.CopperIngot, 20)], TimeSpan.FromMinutes(4), 100,
            [Items.SolarPanel, Items.Battery, Items.ReedFence]),
        new(SolarTower, "Elektrownia słoneczna", "Wieża z lustrami na pustyni albo w oazie daje za dnia 600 kW.",
            [SolarPower], [(Items.Mirror, 20), (Items.SteelIngot, 30), (Items.StoneBlocks, 20)], TimeSpan.FromMinutes(6), 150,
            [Items.SolarTower]),
        new(WaterSupply, "Wodociągi", "Studnia głębinowa pompuje wodę spod ziemi, a odsalarka na wybrzeżu robi ją z morza.",
            [Automation], [(Items.IronIngot, 20), (Items.CopperIngot, 10), (Items.Stone, 30)], TimeSpan.FromMinutes(2), 50,
            [Items.WaterWell, Items.Desalinator]),
        new(BuildingMaterials, "Materiały budowlane", "Suszarnia robi cegłę z piasku, gliny i wody, piec cementowy cement, a betoniarka beton.",
            [WaterSupply], [(Items.Clay, 20), (Items.Stone, 40), (Items.Sand, 40)], TimeSpan.FromMinutes(3), 60,
            // Only the machines: adobe bricks, cement and concrete go into buildings of other branches and city orders, so the
            // trader and other players sell them to anyone.
            [Items.DryingYard, Items.CementKiln, Items.ConcreteMixer]),
        new(WindPower, "Energia wiatrowa", "Turbina wiatrowa daje prąd bez paliwa, tyle, ile wieje wiatr: najwięcej na lodowcu, stepie i tundrze.",
            [Automation], [(Items.IronIngot, 30), (Items.CopperIngot, 20), (Items.Planks, 20)], TimeSpan.FromMinutes(2), 50,
            [Items.WindTurbine]),
        new(LargeWind, "Duże turbiny wiatrowe", "Duża turbina na 3 × 3 kafelkach daje w pełnym wietrze 400 kW.",
            [WindPower, BuildingMaterials], [(Items.SteelIngot, 40), (Items.Concrete, 20), (Items.Iridium, 2)], TimeSpan.FromMinutes(6), 150,
            [Items.LargeWindTurbine]),
        new(PowerDistribution, "Przesył energii",
            "Transformator, podstacja zasilająca pole 9 × 9, słup wysokiego napięcia łączący się z innymi z 20 pól i przyłącze sieciowe: kabel do przyłącza na innej działce, także cudzej, za opłatą za prąd.",
            [Hydropower, Metallurgy], [(Items.CopperIngot, 60), (Items.SteelIngot, 20), (Items.IronIngot, 40)], TimeSpan.FromMinutes(4), 80,
            [Items.Transformer, Items.Substation, Items.Pylon, Items.GridTie]),
        new(EmissionControl, "Filtry spalin", "Piece, generatory i elektrownie dymią, a smog spowalnia uprawy na działce i wokół niej. Filtr spalin zabiera część dymu.",
            [BrickFurnaces], [(Items.Charcoal, 40), (Items.IronIngot, 40)], TimeSpan.FromMinutes(4), 50,
            [Items.Scrubber]),
        new(ThermalPower, "Energetyka cieplna", "Elektrownia węglowa na 3 × 3 polach daje 1 MW z węgla.",
            [PowerDistribution, BuildingMaterials], [(Items.Coal, 200), (Items.SteelIngot, 60), (Items.Concrete, 30), (Items.Transformer, 2)],
            TimeSpan.FromMinutes(8), 150, [Items.CoalPlant]),
        new(EnergyStorage, "Magazynowanie energii", "Magazyn energii mieści tyle co dziesięć akumulatorów i oddaje do 400 kW.",
            [PowerDistribution, SolarPower], [(Items.Battery, 10), (Items.SteelIngot, 40), (Items.Transformer, 4)], TimeSpan.FromMinutes(8), 150,
            [Items.EnergyStore]),
        new(NuclearPower, "Energetyka jądrowa", "Elektrownia jądrowa na 4 × 4 polach daje 2,5 MW z prętów paliwowych z rudy uranu, która trafia się w głębokich kopalniach.",
            [ThermalPower, DeepMining, Petrochemistry, Alternator, PressureVessel, ControlModule],
            [(Items.UraniumOre, 40), (Items.PrecastConcrete, 40), (Items.SteelIngot, 200), (Items.ControlModule, 2)], TimeSpan.FromMinutes(30), 220,
            [Items.NuclearPlant, Items.FuelRod]),
        new(Heating, "Ogrzewanie", "Kocioł grzewczy na drewno, węgiel albo pellet grzeje maszyny wokół siebie, a topielnik zamienia lód w wodę.",
            [], [(Items.IronIngot, 15), (Items.Stone, 30), (Items.Coal, 20)], TimeSpan.FromSeconds(90), 30,
            [Items.Heater, Items.IceMelter]),
        new(Refrigeration, "Chłodnictwo", "Chłodnia mrozi ryby i robi lody: na lodowcu za darmo, gdzie indziej na bloku lodu za każdą partię.",
            [Heating], [(Items.Ice, 30), (Items.IronIngot, 20), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(2), 50,
            [Items.ColdStore, Items.FrozenFish, Items.FrozenSalmon, Items.IceCream]),
        new(Glaciology, "Glacjologia", "Wiertnica lodowa wyciąga rdzenie z lodowca, a stacja polarna robi z nich dane naukowe, które przyspieszają badania.",
            [Heating, Automation], [(Items.Ice, 40), (Items.SteelIngot, 15), (Items.CopperIngot, 15)], TimeSpan.FromMinutes(3), 80,
            [Items.IceCoreDrill, Items.PolarStation, Items.IceCore, Items.ResearchData]),
        new(DataCenter, "Centrum danych", "Serwerownia na prąd liczy dla badań; na lodowcu chłodzi ją powietrze, gdzie indziej lód.",
            [SiliconWorks], [(Items.SiliconWafer, 20), (Items.OpticalFiber, 10), (Items.CopperIngot, 30)], TimeSpan.FromMinutes(5), 120,
            [Items.ServerRoom, Items.ComputingPower]),
        new(PolarTourism, "Turystyka polarna", "Hotel lodowy i punkt widokowy na lodowcu albo w tundrze zarabiają na turystach, trzy razy więcej przy zorzy.",
            [Heating], [(Items.Ice, 60), (Items.Planks, 30), (Items.Wool, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.IceHotel, Items.Viewpoint]),
        new(PolarTransport, "Transport polarny", "Psi zaprzęg i skuter śnieżny wiozą ładunki z lodowca bez torów, a fundament pozwala pociągom jeździć po lodzie pełną prędkością.",
            [], [(Items.Planks, 30), (Items.Wool, 10), (Items.IronIngot, 20)], TimeSpan.FromMinutes(2), 40,
            [Items.DogKennel, Items.SnowmobileGarage, Items.TrackFoundation]),
        new(Assembly, "Montaż", "Hala montażowa na prąd składa płytki drukowane i silniki z towarów innych biomów; w mieście o 25% szybciej.",
            [Automation, Petrochemistry], [(Items.SteelIngot, 30), (Items.Plastic, 20), (Items.CopperIngot, 20)], TimeSpan.FromMinutes(4), 100,
            [Items.AssemblyHall, Items.CircuitBoard, Items.ElectricMotor, Items.Bicycle, Items.Radio]),
        new(Appliances, "AGD i motoryzacja", "Hala montażowa składa pralki dla dużych miast i samochody, na które czekają metropolie.",
            [Assembly, Gearbox, PaintedSheet], [(Items.ElectricMotor, 20), (Items.CircuitBoard, 20), (Items.SteelIngot, 200)], TimeSpan.FromMinutes(10), 150,
            [Items.WashingMachine, Items.Car]),
        new(Commerce, "Handel", "Sklep w mieście sprzedaje twoje towary mieszkańcom przez całą dobę, po cenach, które sam ustawisz.",
            [], [(Items.Planks, 20), (Items.Bread, 20), (Items.Glass, 10)], TimeSpan.FromMinutes(2), 40,
            [Items.Shop]),
        new(UrbanConstruction, "Budownictwo miejskie", "Działka w mieście jest ciasna, więc buduje się w górę: magazyn wysokiego składowania i fabryka wielopiętrowa.",
            [BuildingMaterials, Assembly], [(Items.Concrete, 40), (Items.SteelIngot, 30), (Items.Glass, 20)], TimeSpan.FromMinutes(6), 150,
            [Items.HighBay, Items.MultistoreyFactory]),
        new(CityServices, "Usługi", "Biuro projektowe, laboratorium i warsztat serwisowy pracują dla ciebie i sprzedają usługi innym graczom w mieście.",
            [], [(Items.Glass, 20), (Items.Planks, 30), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.DesignOffice, Items.Laboratory, Items.ServiceWorkshop, Items.Blueprint]),
        new(WasteManagement, "Gospodarka odpadami", "Odbierasz śmieci z miasta za opłatę, a sortownia miejska na prąd rozdziela je na surowce wtórne.",
            [Sorting], [(Items.IronIngot, 20), (Items.Trash, 50)], TimeSpan.FromMinutes(2), 40,
            [Items.CitySorter]),
        new(FreightHub, "Centrum logistyczne", "Dworzec towarowy w mieście obniża opłaty kolejowe działki o 25% i pozwala jej wysyłać 10 kursów zamiast 5.",
            [Logistics, BuildingMaterials], [(Items.Concrete, 30), (Items.SteelIngot, 20), (Items.Bricks, 30)], TimeSpan.FromMinutes(4), 80,
            [Items.FreightTerminal]),
        new(ExportMarket, "Rynek skupu", "Rynek skupu w mieście (jeden na miasto) wystawia na rynek graczy towary, które co godzinę przywozi pociąg ze skrzyni eksportowej przy stacji; właściciel rynku bierze prowizję od cudzych towarów.",
            [Commerce, FreightHub], [(Items.Concrete, 40), (Items.Planks, 40), (Items.Bread, 40), (Items.Blueprint, 2)], TimeSpan.FromMinutes(5), 100,
            [Items.BuyMarket, Items.ExportChest]),
        new(RailFerry, "Port pociągowy", "Port pociągowy na wybrzeżu przeprawia pociągi z towarem na eksport przez morze, gdzie jadą trzy razy wolniej niż po lądzie; właściciel portu ustala opłatę za przeprawę.",
            [ExportMarket, Seafaring], [(Items.SteelIngot, 30), (Items.Beams, 30), (Items.Concrete, 30)], TimeSpan.FromMinutes(5), 100,
            [Items.RailPort]),
        new(Headquarters, "Siedziba spółki", "Siedziba w mieście pozwala spółce przyjąć 30 członków zamiast 20 i potraja jej wspólny magazyn.",
            [CityServices], [(Items.Concrete, 40), (Items.MarbleSlabs, 20), (Items.Blueprint, 2)], TimeSpan.FromMinutes(5), 100,
            [Items.CompanyHq]),
        new(Tailoring, "Krawiectwo", "Pracownia krawiecka w mieście szyje ubrania, które przyspieszają pracę w leśnictwie, górnictwie, rolnictwie, rybołówstwie i rzemiośle albo poprawiają ceny u handlarza.",
            [], [(Items.Wool, 40), (Items.Planks, 20), (Items.IronIngot, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.Tailor, .. Items.Clothes.Keys]),
        new(Silviculture, "Hodowla lasu", "Sadzonki świerku, brzozy, dębu i lipy u handlarza i wieża obserwacyjna, z której widać pożar, zanim się rozprzestrzeni.",
            [ForestryIndustry], [(Items.Sapling, 20), (Items.Wood, 40)], TimeSpan.FromSeconds(90), 30,
            [Items.SaplingSpruce, Items.SaplingBirch, Items.SaplingOak, Items.SaplingLinden, Items.Watchtower]),
        new(Papermaking, "Papiernictwo", "Papiernia na prąd robi masę celulozową z pelletu i wody, z niej papier, a z makulatury karton.",
            [Woodworking], [(Items.WoodPellets, 40), (Items.IronIngot, 20), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.PaperMill, Items.Pulp, Items.Paper, Items.Cardboard]),
        new(Furniture, "Meblarstwo", "Stolarnia robi krzesła, stoły i szafy, a z drewna dębowego komody, na które czekają miasta.",
            [ForestryIndustry], [(Items.Planks, 40), (Items.Nails, 30)], TimeSpan.FromMinutes(2), 40,
            [Items.Carpentry, Items.Chair, Items.Table, Items.Wardrobe, Items.OakDresser]),
        new(Charring, "Węglarstwo i smolarstwo", "Mielerz wypala z drewna węgiel drzewny, który pali się jak kopalny i zastępuje go w odlewni przy stali, a smolarnia warzy z żywicy smołę, terpentynę i kalafonię.",
            [ForestryIndustry], [(Items.Wood, 60), (Items.Stone, 40)], TimeSpan.FromMinutes(2), 40,
            [Items.CharcoalKiln, Items.Charcoal, Items.TarKiln, Items.Tar, Items.Turpentine, Items.Rosin]),
        new(Beekeeping, "Pszczelarstwo", "Ul daje miód i wosk, szybciej przy kwitnących lipach; z miodu pieczesz pierniki, z wosku robisz świece.",
            [], [(Items.Planks, 20), (Items.Sapling, 5)], TimeSpan.FromSeconds(90), 30,
            [Items.Beehive, Items.Gingerbread, Items.Candles]),
        new(ForestFoods, "Leśna spiżarnia", "Kuchnia gotuje zupę grzybową, pierogi z jagodami i herbatę z ziół zebranych w lesie.",
            [Cooking], [(Items.Mushrooms, 20), (Items.Berries, 20)], TimeSpan.FromSeconds(90), 30,
            [Items.MushroomSoup, Items.BerryPierogi, Items.HerbalTea]),
        new(FireProtection, "Ochrona lasu", "Strażnica chroni drzewa wokół siebie przed ogniem, a zakłady chemiczne robią z terpentyny środek na kornika.",
            [Silviculture, Charring], [(Items.Bricks, 20), (Items.IronIngot, 20), (Items.Turpentine, 5)], TimeSpan.FromMinutes(3), 60,
            [Items.FireStation, Items.Insecticide]),
        // Husbandry and gastronomy, which the other industries need.
        new(Breeding, "Rozród i obornik", "Dwa zwierzęta jednego gatunku na sąsiednich polach mają młode, a obornik spod krów, owiec i kur przyspiesza wzrost upraw o 25%.",
            [], [(Items.Wheat, 40), (Items.Corn, 20)], TimeSpan.FromSeconds(90), 30,
            [Items.Manure]),
        new(Dairying, "Mleczarstwo", "Mleczarnia robi z mleka ser, masło i kazeinę, a z kazeiny tworzywo i farbę.",
            [], [(Items.Milk, 40), (Items.Planks, 10)], TimeSpan.FromMinutes(2), 40,
            [Items.Dairy, Items.Cheese, Items.Butter, Items.Casein]),
        new(Butchery, "Rzeźnictwo i garbarstwo", "Rzeźnia daje z krowy mięso, skórę, łój i kości; ze skóry szyjesz miech, który przyspiesza piece i odlewnię o 15%.",
            [Breeding], [(Items.Cow, 1), (Items.Bricks, 20)], TimeSpan.FromMinutes(3), 60,
            [Items.Slaughterhouse, Items.Meat, Items.Hide, Items.Tallow, Items.Bones, Items.Bellows, Items.DriedMeat]),
        new(Biogas, "Biogaz", "Biogazownia fermentuje obornik na gaz i nawóz.",
            [Breeding], [(Items.Manure, 40), (Items.Bricks, 20), (Items.IronIngot, 10)], TimeSpan.FromMinutes(4), 80,
            [Items.BiogasPlant]),
        new(Distilling, "Gorzelnictwo", "Gorzelnia pędzi z ziemniaków etanol, który pali się w agregatach i skuterach, a wywar idzie na paszę.",
            [], [(Items.Potato, 60), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(3), 60,
            [Items.PotatoDistillery, Items.Ethanol, Items.Stillage]),
        new(Horsemanship, "Koniarstwo", "Konie robocze ze stajni przyspieszają ścinkę i kopanie o 25% i ciągną wozy do 50 km bez kolei.",
            [Breeding, Distilling], [(Items.Corn, 40), (Items.Planks, 20), (Items.Stillage, 10)], TimeSpan.FromMinutes(4), 80,
            [Items.Stable, Items.Horse, Items.ConcentrateFeed]),
        new(WorksCanteens, "Stołówki zakładowe", "Stołówka karmi załogi maszyn działki: póki ma jedzenie, maszyny na opał i na prąd pracują o 20% szybciej.",
            [Cooking], [(Items.Bigos, 20), (Items.Bread, 20)], TimeSpan.FromMinutes(3), 60,
            [Items.Canteen, Items.WorkersLunch]),
        new(WorkMeals, "Posiłki regeneracyjne", "Kanapki, drugie śniadanie górnika i gulasz drwala przyspieszają pracę postaci na dłużej niż chleb.",
            [Cooking, Dairying], [(Items.Cheese, 10), (Items.Bread, 10)], TimeSpan.FromMinutes(2), 40,
            [Items.CheeseSandwich, Items.MinersBreakfast, Items.LumberjackGoulash]),
        new(FineCuisine, "Kuchnia wykwintna", "Pieczeń i sernik na bankiety, festyny, debiut spółki na giełdzie i do restauracji hotelu i punktu widokowego.",
            [Butchery, Dairying], [(Items.Meat, 20), (Items.Cheese, 20), (Items.Butter, 10)], TimeSpan.FromMinutes(5), 100,
            [Items.RoastBeef, Items.Cheesecake, Items.DiningTakings]),
        new(BioLab, "Laboratorium biologiczne", "Żelatyna z kości i pożywka laboratoryjna, która przyspiesza badania.",
            [Butchery], [(Items.Bones, 40), (Items.Egg, 20)], TimeSpan.FromMinutes(3), 60,
            [Items.Gelatin, Items.CultureMedium]),
        new(Farmsteads, "Zagroda rolnika", "Zagroda sama sieje wybraną uprawę na polach wokół siebie i zbiera plony.",
            [], [(Items.Wheat, 40), (Items.Wood, 40)], TimeSpan.FromSeconds(90), 30,
            [Items.Farmstead]),
        new(ManorFarms, "Folwark", "Folwark obrabia większe pole, sieje szybciej rosnące uprawy i mieści więcej plonów.",
            [Farmsteads, Breeding], [(Items.Wheat, 100), (Items.Bricks, 20), (Items.Provisions, 5)], TimeSpan.FromMinutes(4), 80,
            [Items.ManorFarm]),
        new(Feeders, "Karmniki", "Karmnik sam karmi głodne zwierzęta do 3 pól wokół siebie paszą wsypaną ręcznie albo podajnikiem.",
            [], [(Items.Wheat, 30), (Items.Planks, 20)], TimeSpan.FromSeconds(90), 30,
            [Items.Feeder]),
        // The raw-material branches grown further. Mining: geology (a lasting bonus), coke and the precious metals.
        new(Geology, "Geologia złożowa",
            "Geolodzy czytają skały twoich działek: minerał działki (siarka, sól, marmur albo kwarc) trafia się w kopalniach i wiertłach dwa razy częściej, a badanie geologiczne kosztuje połowę.",
            [MiningAutomation], [(Items.Stone, 200), (Items.IronIngot, 40), (Items.StoneBlocks, 20)], TimeSpan.FromMinutes(3), 60,
            [], Icon: Items.Quartz),
        new(Coking, "Koksownictwo",
            "Koksownia (3 × 3 pola) wypala z 4 węgli 3 koksu i smołę. Koks grzeje jak dwa węgle: w piecach, odlewni, maszynach na opał i elektrowniach.",
            [BlastFurnaces], [(Items.Coal, 200), (Items.Bricks, 40), (Items.SteelIngot, 20)], TimeSpan.FromMinutes(4), 80,
            [Items.CokeOven, Items.Coke]),
        new(PreciousMetals, "Metale szlachetne",
            "Kruszarki kruszą też rudę srebra i złota, z której wytopisz więcej sztabek, a pracownia jubilerska robi biżuterię ze złota i bursztynu i srebrną zastawę.",
            [ConeCrushing], [(Items.SilverOre, 20), (Items.GoldOre, 2), (Items.SteelIngot, 30)], TimeSpan.FromMinutes(6), 120,
            [Items.CrushedSilverOre, Items.CrushedGoldOre, Items.Jeweler, Items.Jewelry, Items.Silverware]),
        // Forestry.
        new(TreeNursery, "Szkółka leśna",
            "Szkółka (2 × 2 pola) hoduje sadzonki dla leśników wokół siebie: las chaty, leśniczówki albo nadleśnictwa do 8 pól od niej rośnie o połowę szybciej.",
            [Silviculture], [(Items.Sapling, 40), (Items.Planks, 30)], TimeSpan.FromMinutes(2), 40,
            [Items.TreeNursery]),
        // Husbandry.
        new(Textiles, "Przędzalnia i tkalnia",
            "Przędzalnia na prąd przędzie wełnę na przędzę, a krosno tka z przędzy sukno, na które czekają miasta.",
            [Breeding], [(Items.Wool, 40), (Items.Planks, 20), (Items.IronIngot, 20)], TimeSpan.FromMinutes(3), 60,
            [Items.SpinningMill, Items.Loom, Items.Yarn, Items.Cloth]),
        new(AutoBarns, "Obora automatyczna",
            "Obora automatyczna (2 × 2 pola) karmi zwierzęta do 4 pól wokół siebie jak karmnik i sama zbiera, co dają: jajka, wełnę, mleko, obornik i młode. Podajnik je wyjmuje.",
            [Feeders, Automation], [(Items.Feeder, 4), (Items.Inserter, 4), (Items.Milk, 20)], TimeSpan.FromMinutes(4), 80,
            [Items.AutoBarn]),
        new(PigsAndGoats, "Świnie i kozy",
            "Świnie jedzą ziemniaki, kukurydzę, wywar i wysłodki i dają mięso bez rzeźni; kozy dają mleko szybciej niż krowy, choć mniej naraz.",
            [Breeding], [(Items.Potato, 60), (Items.Corn, 40), (Items.Planks, 30)], TimeSpan.FromMinutes(3), 60,
            [Items.Pig, Items.Goat]),
        // Farming.
        new(Irrigation, "Nawadnianie",
            "Zraszacz podlewa pola do 4 pól wokół siebie: uprawy rosną na nich o 30% szybciej, także w zagrodzie, której pola sięga, a na stepie i pustyni da się tam w ogóle orać.",
            [Farmsteads, WaterSupply], [(Items.Water, 50), (Items.Pipe, 20), (Items.Wheat, 100)], TimeSpan.FromMinutes(3), 60,
            [Items.Sprinkler]),
        new(MechanizedFarming, "Mechanizacja rolnictwa",
            "Gospodarstwo zmechanizowane (4 × 4 pola) obrabia największe pole, a ciągniki i kombajny sieją i zbierają dwa razy szybciej niż w zagrodzie.",
            [ManorFarms, Combustion], [(Items.Diesel, 40), (Items.SteelIngot, 60), (Items.ElectricMotor, 4)], TimeSpan.FromMinutes(8), 150,
            [Items.MechanizedFarm]),
        new(SugarBeets, "Buraki cukrowe",
            "Nowa uprawa: burak cukrowy. Cukrownia warzy z niego cukier, a wysłodki idą na paszę; z cukru piec piekarski piecze pączki.",
            [Farmsteads], [(Items.Potato, 40), (Items.Carrot, 40), (Items.Bricks, 10)], TimeSpan.FromMinutes(2), 40,
            [Items.SugarBeet, Items.SugarFactory, Items.Sugar, Items.BeetPulp, Items.Donuts]),
        new(FiberCrops, "Len i konopie",
            "Nowe uprawy: len, z którego krosno tka płótno, i konopie, z których przędzalnia skręca liny.",
            [Farmsteads, Textiles], [(Items.Wheat, 60), (Items.Yarn, 20)], TimeSpan.FromMinutes(3), 60,
            [Items.Flax, Items.Hemp, Items.Linen, Items.Rope]),
        // Energy.
        new(BiomassPower, "Elektrownia na biomasę",
            "Elektrownia na biomasę (3 × 3 pola) daje 500 kW z drewna, pelletu albo węgla drzewnego.",
            [ThermalPower, ForestryIndustry], [(Items.WoodPellets, 200), (Items.SteelIngot, 40), (Items.Transformer, 2)], TimeSpan.FromMinutes(8), 150,
            [Items.BiomassPlant]),
        new(Cogeneration, "Kogeneracja",
            "Elektrociepłownia (3 × 3 pola) daje 700 kW z węgla, koksu albo drewna, a na lodowcu grzeje przy tym maszyny do 6 pól od siebie.",
            [ThermalPower, Heating], [(Items.Coal, 200), (Items.Pipe, 30), (Items.Heater, 2), (Items.SteelIngot, 40)], TimeSpan.FromMinutes(8), 150,
            [Items.ChpPlant]),
        new(GeothermalPower, "Geotermia",
            "Elektrownia geotermalna (3 × 3 pola) w górach, na tundrze albo lodowcu daje bez paliwa 250 kW dzień i noc, na lodowcu więcej.",
            [DeepMining, PowerDistribution], [(Items.MineSupport, 20), (Items.SteelIngot, 60), (Items.Transformer, 2)], TimeSpan.FromMinutes(10), 180,
            [Items.GeothermalPlant]),
        new(HydrogenPower, "Wodór",
            "Elektrolizer na prąd robi z wody wodór, a ogniwo paliwowe oddaje z niego 70% tego prądu, kiedy trzeba. Wodór wozi się jak każdy towar.",
            [EnergyStorage, WaterSupply], [(Items.Water, 100), (Items.Battery, 10), (Items.SilverIngot, 10), (Items.CopperIngot, 60)], TimeSpan.FromMinutes(10), 180,
            [Items.Electrolyzer, Items.FuelCell, Items.Hydrogen]),
        // The crafting tree, level by level: each technology unlocks one item and needs the technologies of its ingredients first.
        new(Lubricant, "Smar techniczny", "Smaruje łożyska, przekładnie i siłowniki.",
            [Refining], [(Items.Kerosene, 10), (Items.Wax, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.Lubricant]),
        new(ElectricWire, "Przewód elektryczny", "Miedź w izolacji z tworzywa: z niej są cewki, wiązki i drut nadprzewodzący.",
            [MachineShop], [(Items.CopperIngot, 5), (Items.Plastic, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.ElectricWire]),
        new(GlassLaminate, "Laminat szklany", "Włókno szklane w żywicy: lekki i twardy, na cewki i kompozyty.",
            [Petrochemistry], [(Items.Glass, 10), (Items.Resin, 5), (Items.Plastic, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.GlassLaminate]),
        new(Paint, "Farba", "Terpentyna, olej i glina na pigment; chroni blachę przed rdzą.",
            [Charring], [(Items.Turpentine, 5), (Items.SunflowerOil, 5), (Items.Clay, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.Paint]),
        new(CaseinGlue, "Klej kazeinowy", "Klej z mleka i kalafonii: skleja kompozyty, wiązki i panele.",
            [Charring], [(Items.Milk, 10), (Items.Rosin, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.CaseinGlue]),
        new(InsulationBoard, "Płyta izolacyjna", "Wełna między sklejką: trzyma ciepło i zimno, na obudowy i zbiorniki.",
            [Furniture], [(Items.Wool, 10), (Items.Plywood, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.InsulationBoard]),
        new(Firebrick, "Cegła szamotowa", "Cegła z gliny i kwarcu, która wytrzymuje ogień pieców i dysz.",
            [Metallurgy], [(Items.Clay, 10), (Items.Quartz, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.Firebrick]),
        new(PrecastConcrete, "Prefabrykat żelbetowy", "Beton ze stalowym zbrojeniem: fundament wielkich budynków.",
            [BuildingMaterials], [(Items.Concrete, 20), (Items.SteelIngot, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.PrecastConcrete]),
        new(Coolant, "Płyn chłodniczy", "Schłodzony lodem glikol z gazu: chłodzi rdzenie, magnesy i reaktor.",
            [Petrochemistry], [(Items.NaturalGas, 10), (Items.Water, 10), (Items.Ice, 10)], TimeSpan.FromMinutes(2), 20,
            [Items.Coolant]),
        new(IridiumAlloy, "Stop irydowy", "Iryd i żelazo meteorytowe z lodowca stopione ze stalą.",
            [SiliconWorks], [(Items.Iridium, 5), (Items.MeteoricIron, 10), (Items.SteelIngot, 10)], TimeSpan.FromMinutes(2), 20,
            [Items.IridiumAlloy]),
        new(Provisions, "Prowiant", "Kuchnia polowa, piec piekarski i wędzarnia pakują prowiant dla załóg wielkich budów i stacji orbitalnej.",
            [Cooking], [(Items.CannedFish, 5), (Items.Bread, 10), (Items.Cardboard, 5)], TimeSpan.FromMinutes(2), 20,
            [Items.Provisions]),
        new(Bearing, "Łożysko", "Stalowe kulki w smarze: bez nich nic się nie kręci.",
            [Lubricant, MachineShop], [(Items.SteelIngot, 5), (Items.Lubricant, 5)], TimeSpan.FromMinutes(3), 40,
            [Items.Bearing]),
        new(PaintedSheet, "Blacha lakierowana", "Stalowa blacha pomalowana na obudowy i zbiorniki.",
            [Paint, MachineShop], [(Items.SteelIngot, 5), (Items.Paint, 5)], TimeSpan.FromMinutes(3), 40,
            [Items.PaintedSheet]),
        new(Coil, "Cewka", "Przewód nawinięty na laminat: serce wirnika i magnesu.",
            [ElectricWire, GlassLaminate, MachineShop], [(Items.ElectricWire, 30), (Items.GlassLaminate, 5)], TimeSpan.FromMinutes(3), 40,
            [Items.Coil]),
        new(WiringHarness, "Wiązka kablowa", "Przewody związane w jedną wiązkę, która łączy maszynę w całość.",
            [ElectricWire, CaseinGlue, MachineShop], [(Items.ElectricWire, 40), (Items.SyntheticRubber, 5), (Items.CaseinGlue, 5)], TimeSpan.FromMinutes(3), 40,
            [Items.WiringHarness]),
        new(RefractoryLining, "Okładzina ogniotrwała", "Wyłożenie z cegły szamotowej na komory spalania i plazmy.",
            [Firebrick, PrecastConcrete, Stonemasonry], [(Items.Firebrick, 40), (Items.PrecastConcrete, 5)], TimeSpan.FromMinutes(3), 40,
            [Items.RefractoryLining]),
        new(CarbonComposite, "Kompozyt węglowy", "Laminat z węglem drzewnym: lżejszy od stali i równie mocny.",
            [GlassLaminate, CaseinGlue, Petrochemistry], [(Items.GlassLaminate, 10), (Items.Charcoal, 10), (Items.CaseinGlue, 5)], TimeSpan.FromMinutes(3), 40,
            [Items.CarbonComposite]),
        new(SuperconductingWire, "Drut nadprzewodzący", "Drut ze stopu irydowego, który w zimnie przewodzi bez strat.",
            [IridiumAlloy, ElectricWire, Coolant, MachineShop], [(Items.IridiumAlloy, 5), (Items.ElectricWire, 20), (Items.Coolant, 10)], TimeSpan.FromMinutes(3), 40,
            [Items.SuperconductingWire]),
        new(RocketFuel, "Paliwo rakietowe", "Schłodzona nafta: rakieta wynosi na orbitę satelitę na dwustu porcjach.",
            [Coolant, Refining], [(Items.Kerosene, 15), (Items.Coolant, 5)], TimeSpan.FromMinutes(3), 40,
            [Items.RocketFuel]),
        new(Gearbox, "Przekładnia", "Koła zębate z brązu na łożyskach: zmienia obroty na siłę.",
            [Bearing, Lubricant, MachineShop], [(Items.BronzeIngot, 6), (Items.Bearing, 6), (Items.Lubricant, 3)], TimeSpan.FromMinutes(4), 60,
            [Items.Gearbox]),
        new(Rotor, "Wirnik", "Cewki na stalowym wale: kręci się w prądnicy i turbopompie.",
            [Coil, Bearing, MachineShop], [(Items.Coil, 6), (Items.Bearing, 6), (Items.SteelIngot, 6)], TimeSpan.FromMinutes(4), 60,
            [Items.Rotor]),
        new(MachineHousing, "Obudowa przemysłowa", "Szczelna, ocieplona skrzynia na prądnice i moduły sterujące.",
            [PaintedSheet, InsulationBoard, MachineShop], [(Items.PaintedSheet, 12), (Items.InsulationBoard, 3), (Items.Gasket, 6)], TimeSpan.FromMinutes(4), 60,
            [Items.MachineHousing]),
        new(CombustionChamber, "Komora spalania", "Stalowa komora wyłożona szamotem, w której pali się paliwo rakietowe.",
            [RefractoryLining, MachineShop], [(Items.RefractoryLining, 6), (Items.SteelIngot, 12), (Items.Gasket, 6)], TimeSpan.FromMinutes(4), 60,
            [Items.CombustionChamber]),
        new(CompositePanel, "Panel kompozytowy", "Lekka i sztywna płyta na kadłuby, zbiorniki i turbiny.",
            [CarbonComposite, InsulationBoard, CaseinGlue, Assembly], [(Items.CarbonComposite, 9), (Items.InsulationBoard, 3), (Items.CaseinGlue, 3)], TimeSpan.FromMinutes(4), 60,
            [Items.CompositePanel]),
        new(SuperconductingMagnet, "Elektromagnes nadprzewodzący", "Magnes, który utrzymuje plazmę w reaktorze i satelitę na kursie.",
            [SuperconductingWire, Coil, Coolant, MachineShop], [(Items.SuperconductingWire, 12), (Items.Coil, 6), (Items.Coolant, 6)], TimeSpan.FromMinutes(4), 60,
            [Items.SuperconductingMagnet]),
        new(Alternator, "Prądnica", "Wirnik w obudowie: zamienia obroty w prąd.",
            [Rotor, MachineHousing, WiringHarness, Assembly], [(Items.Rotor, 3), (Items.MachineHousing, 3), (Items.WiringHarness, 3)], TimeSpan.FromMinutes(6), 80,
            [Items.Alternator]),
        new(ControlModule, "Moduł sterujący", "Płytki drukowane z wyświetlaczem w obudowie: mózg każdej maszyny.",
            [WiringHarness, MachineHousing, Assembly], [(Items.CircuitBoard, 6), (Items.WiringHarness, 3), (Items.WindowPane, 3), (Items.MachineHousing, 3)], TimeSpan.FromMinutes(6), 80,
            [Items.ControlModule]),
        new(HydraulicActuator, "Siłownik hydrauliczny", "Tłok na przekładni, który podnosi i przesuwa ciężary.",
            [Gearbox, Lubricant, MachineShop], [(Items.Gearbox, 3), (Items.Gasket, 6), (Items.Lubricant, 6), (Items.SteelIngot, 6)], TimeSpan.FromMinutes(6), 80,
            [Items.HydraulicActuator]),
        new(RocketNozzle, "Dysza rakietowa", "Rozszerzająca się dysza, przez którą komora spalania pcha rakietę.",
            [CombustionChamber, Firebrick, MachineShop], [(Items.CombustionChamber, 3), (Items.SteelIngot, 6), (Items.Firebrick, 12)], TimeSpan.FromMinutes(6), 80,
            [Items.RocketNozzle]),
        new(PressureVessel, "Zbiornik ciśnieniowy", "Szczelny zbiornik z kompozytu i blachy na gaz pod ciśnieniem.",
            [CompositePanel, PaintedSheet, Assembly], [(Items.CompositePanel, 6), (Items.PaintedSheet, 12), (Items.Gasket, 12)], TimeSpan.FromMinutes(6), 80,
            [Items.PressureVessel]),
        new(ComputingCore, "Rdzeń obliczeniowy", "Moduły sterujące połączone światłowodem: liczy za roboty i rakiety.",
            [ControlModule, Coolant, Assembly], [(Items.ControlModule, 4), (Items.OpticalFiber, 8), (Items.SiliconWafer, 8), (Items.Coolant, 4)], TimeSpan.FromMinutes(8), 100,
            [Items.ComputingCore]),
        new(RobotArm, "Ramię robotyczne", "Ramię z siłownikami, które samo chwyta, przenosi i skręca.",
            [HydraulicActuator, ControlModule, WiringHarness, Assembly], [(Items.HydraulicActuator, 4), (Items.ControlModule, 2), (Items.WiringHarness, 4)], TimeSpan.FromMinutes(8), 100,
            [Items.RobotArm]),
        new(Turbopump, "Turbopompa", "Pompa napędzana własną turbiną: tłoczy paliwo do silnika rakietowego.",
            [Rotor, Gearbox, CombustionChamber, ControlModule, Assembly], [(Items.Rotor, 4), (Items.Gearbox, 2), (Items.CombustionChamber, 2), (Items.ControlModule, 2)], TimeSpan.FromMinutes(8), 100,
            [Items.Turbopump]),
        new(TurbineSet, "Turbozespół", "Turbina z prądnicą: zamienia ciepło reaktora w prąd.",
            [CompositePanel, Alternator, Gearbox, Assembly], [(Items.CompositePanel, 8), (Items.Alternator, 2), (Items.Gearbox, 2)], TimeSpan.FromMinutes(8), 100,
            [Items.TurbineSet]),
        new(CryogenicTank, "Zbiornik kriogeniczny", "Zbiornik, który trzyma paliwo i chłodziwo w głębokim mrozie.",
            [PressureVessel, InsulationBoard, Coolant, ControlModule, Assembly], [(Items.PressureVessel, 2), (Items.InsulationBoard, 8), (Items.Coolant, 8), (Items.ControlModule, 2)], TimeSpan.FromMinutes(8), 100,
            [Items.CryogenicTank]),
        new(IndustrialRobot, "Robot przemysłowy", "Dwa ramiona z własnym rdzeniem i prądnicą: pracuje bez przerwy.",
            [RobotArm, ComputingCore, Alternator, Assembly], [(Items.RobotArm, 4), (Items.ComputingCore, 2), (Items.Alternator, 2)], TimeSpan.FromMinutes(12), 120,
            [Items.IndustrialRobot]),
        new(NavigationSystem, "System nawigacji", "Żyroskop laserowy z luster i rdzeń, który liczy kurs.",
            [ComputingCore, Coil, Assembly], [(Items.ComputingCore, 2), (Items.Mirror, 8), (Items.Coil, 4), (Items.OpticalFiber, 8)], TimeSpan.FromMinutes(12), 120,
            [Items.NavigationSystem]),
        new(RocketEngine, "Silnik rakietowy", "Komora, dysza i turbopompy: cztery takie unoszą człon rakiety.",
            [Turbopump, RocketNozzle, CombustionChamber, ControlModule, Assembly], [(Items.Turbopump, 4), (Items.RocketNozzle, 2), (Items.CombustionChamber, 2), (Items.ControlModule, 2)], TimeSpan.FromMinutes(12), 120,
            [Items.RocketEngine]),
        new(PlasmaChamber, "Komora plazmowa", "Pierścień magnesów, w którym płonie plazma reaktora fuzyjnego.",
            [SuperconductingMagnet, RefractoryLining, PressureVessel, ComputingCore, Assembly], [(Items.SuperconductingMagnet, 16), (Items.RefractoryLining, 8), (Items.PressureVessel, 4), (Items.ComputingCore, 2)], TimeSpan.FromMinutes(12), 120,
            [Items.PlasmaChamber]),
        new(Avionics, "Awionika", "Nawigacja i rdzenie rakiety: prowadzą ją na orbitę.",
            [NavigationSystem, ComputingCore, WiringHarness, RoboticHall], [(Items.NavigationSystem, 1), (Items.ComputingCore, 2), (Items.WiringHarness, 4)], TimeSpan.FromMinutes(20), 140,
            [Items.Avionics]),
        new(PlasmaControl, "Układ sterowania plazmą", "Rdzenie i magnesy, które co chwilę poprawiają kształt plazmy.",
            [ComputingCore, SuperconductingMagnet, ControlModule, IndustrialRobot, RoboticHall], [(Items.ComputingCore, 2), (Items.SuperconductingMagnet, 4), (Items.ControlModule, 2), (Items.IndustrialRobot, 1)], TimeSpan.FromMinutes(20), 140,
            [Items.PlasmaControl]),
        new(RocketStage, "Człon rakiety", "Cztery silniki pod zbiornikami paliwa: rakieta ma dwa takie człony.",
            [RocketEngine, CryogenicTank, Avionics, CompositePanel, RoboticHall], [(Items.RocketEngine, 4), (Items.CryogenicTank, 2), (Items.Avionics, 1), (Items.CompositePanel, 8)], TimeSpan.FromMinutes(30), 160,
            [Items.RocketStage]),
        new(Satellite, "Satelita", "Satelita z panelami słonecznymi: ładunek misji orbitalnej.",
            [Avionics, CompositePanel, SuperconductingMagnet, RoboticHall], [(Items.SolarPanel, 8), (Items.Avionics, 1), (Items.CompositePanel, 4), (Items.SuperconductingMagnet, 1)], TimeSpan.FromMinutes(30), 160,
            [Items.Satellite]),
        new(FusionReactor, "Reaktor fuzyjny", "Dwie komory plazmowe z turbozespołami: serce elektrowni fuzyjnej.",
            [PlasmaChamber, PlasmaControl, TurbineSet, CryogenicTank, RoboticHall], [(Items.PlasmaChamber, 2), (Items.PlasmaControl, 1), (Items.TurbineSet, 2), (Items.CryogenicTank, 2)], TimeSpan.FromMinutes(30), 160,
            [Items.FusionReactor]),
        new(LaunchRocket, "Rakieta nośna", "Dwa człony pod owiewką z kompozytu: wynosi satelitę z kosmodromu spółki.",
            [RocketStage, Avionics, CompositePanel, RoboticHall], [(Items.RocketStage, 2), (Items.Avionics, 1), (Items.CompositePanel, 6)], TimeSpan.FromMinutes(45), 180,
            [Items.LaunchRocket]),
        new(MachineShop, "Warsztat mechaniczny", "Na prąd (40 kW): robi przewody, łożyska, cewki, przekładnie, wirniki, siłowniki i inne części maszyn.",
            [Assembly], [(Items.SteelIngot, 6), (Items.ElectricMotor, 2), (Items.CopperIngot, 10)], TimeSpan.FromMinutes(2), 60,
            [Items.MachineShop]),
        new(RoboticHall, "Zrobotyzowana hala", "Na 3 × 3 polach, na prąd (300 kW): roboty składają awionikę, człony rakiet, satelity i reaktory fuzyjne.",
            [IndustrialRobot, ComputingCore, PrecastConcrete], [(Items.IndustrialRobot, 3), (Items.ExpressBelt, 10), (Items.ComputingCore, 1), (Items.PrecastConcrete, 20)], TimeSpan.FromMinutes(20), 180,
            [Items.RoboticHall]),
        new(OxygenConverter, "Konwerter tlenowy", "Na 3 × 3 polach, na prąd (100 kW): przedmuchuje żelazo tlenem i robi 2 sztabki stali z 3 sztabek żelaza.",
            [RefractoryLining, HydraulicActuator, ControlModule], [(Items.RefractoryLining, 8), (Items.HydraulicActuator, 2), (Items.ControlModule, 1), (Items.SteelIngot, 20)], TimeSpan.FromMinutes(8), 140,
            [Items.OxygenConverter]),
        new(RollingMill, "Walcownia", "Na 3 × 3 polach, na prąd (120 kW): walcuje blachę i ciągnie przewody dwa razy wydajniej niż warsztat.",
            [Gearbox, Alternator, ControlModule, PrecastConcrete], [(Items.Gearbox, 4), (Items.Alternator, 2), (Items.ControlModule, 1), (Items.PrecastConcrete, 20)], TimeSpan.FromMinutes(8), 140,
            [Items.RollingMill]),
        new(ControlCenter, "Centrum sterowania", "Na prąd (60 kW): wszystkie maszyny przetwórcze na działce pracują o 20% szybciej. Jedno na działkę.",
            [ComputingCore, ControlModule, MachineHousing], [(Items.ComputingCore, 4), (Items.ControlModule, 4), (Items.OpticalFiber, 20), (Items.MachineHousing, 1)], TimeSpan.FromMinutes(12), 160,
            [Items.ControlCenter]),
        new(ContinuousCaster, "Odlewnia ciągła", "Na 3 × 3 polach, na prąd (150 kW): odlewa brąz i stop irydowy wydajniej niż odlewnia i piec łukowy.",
            [IndustrialRobot, RefractoryLining, Coolant], [(Items.IndustrialRobot, 1), (Items.RefractoryLining, 10), (Items.Coolant, 20), (Items.SteelIngot, 20)], TimeSpan.FromMinutes(20), 180,
            [Items.ContinuousCaster]),
        new(AutomatedWarehouse, "Magazyn zautomatyzowany", "Na 3 × 3 polach: 120 miejsc po 500 sztuk, roboty same układają wszystko, co przywiozą taśmy.",
            [IndustrialRobot, ComputingCore, PrecastConcrete], [(Items.IndustrialRobot, 2), (Items.ComputingCore, 2), (Items.ExpressBelt, 20), (Items.PrecastConcrete, 30)], TimeSpan.FromMinutes(20), 180,
            [Items.AutomatedWarehouse]),
        new(SteelCombine, "Kombinat hutniczy", "Na 5 × 5 polach, na prąd (600 kW): robi wszystko, co konwerter, walcownia i odlewnia ciągła, trzy razy szybciej.",
            [OxygenConverter, RollingMill, ContinuousCaster, ControlCenter], [(Items.OxygenConverter, 1), (Items.RollingMill, 1), (Items.ContinuousCaster, 1), (Items.ControlCenter, 1)], TimeSpan.FromMinutes(30), 200,
            [Items.SteelCombine]),
        new(Megafactory, "Megafabryka", "Na 5 × 5 polach, na prąd (1 MW): robi wszystko, co warsztat mechaniczny, hala montażowa i zrobotyzowana hala, trzy razy szybciej.",
            [RoboticHall, ControlCenter, AutomatedWarehouse, PrecastConcrete], [(Items.RoboticHall, 1), (Items.ControlCenter, 1), (Items.AutomatedWarehouse, 1), (Items.PrecastConcrete, 50)], TimeSpan.FromMinutes(30), 200,
            [Items.Megafactory]),
        new(FusionPlant, "Elektrownia fuzyjna", "Na 3 × 3 polach: daje 5 MW, a za paliwo zużywa tylko płyn chłodniczy, jedną porcję na minutę pełnej mocy.",
            [FusionReactor, PrecastConcrete, Alternator, IndustrialRobot], [(Items.FusionReactor, 1), (Items.PrecastConcrete, 25), (Items.Alternator, 2), (Items.IndustrialRobot, 1)], TimeSpan.FromMinutes(45), 220,
            [Items.FusionPlant]),
        new(Megaprojects, "Wielkie budowy", "Tylko dla spółek: członkowie razem stawiają platformę wiertniczą, port, pole lustrzane, kolej międzykontynentalną, a na końcu kosmodrom, misję i stację orbitalną.",
            [BuildingMaterials, Headquarters], [(Items.Concrete, 500), (Items.SteelIngot, 500), (Items.Blueprint, 10)], TimeSpan.FromMinutes(30), 300,
            [], CompanyOnly: true),
        new(CorporateFinance, "Finanse korporacyjne", "Tylko dla spółek: spółka wchodzi na giełdę, a gracze spoza niej kupują jej akcje.",
            [Commerce, Headquarters], [(Items.Paper, 500), (Items.Blueprint, 10), (Items.CircuitBoard, 50)], TimeSpan.FromMinutes(20), 200,
            [], CompanyOnly: true),
        new(Banking, "Bankowość", "Tylko dla spółek: spółka stawia w mieście bank, a w innych miastach jego oddziały; gracze trzymają w nim pieniądze na kontach i robią przelewy.",
            [CorporateFinance], [], TimeSpan.FromMinutes(20), 200,
            [Items.Bank, Items.BankBranch], CompanyOnly: true, CostCents: 100_000_000_000_00),
    ];

    /// <summary>
    /// The branches of industry, each a tree of its own in the client: a tab per branch, in this order.
    /// A technology may need one from another branch; the client shows that one at the side of the tree.
    /// </summary>
    public static readonly IReadOnlyList<Branch> Branches =
    [
        new("logistics", "Logistyka", Items.Belt,
            [Logistics, Automation, Storage, Sorting, FastLogistics, UndergroundBelts, LongInserters, BiggerGrippers, ExpressLogistics,
             IndustrialGrippers, LogisticsDepot, CableTransport, ExpressCable, Drones, RailFerry, RailFreight, RailFreight2, RailFreight3, RailFreight4, Assembling, FastAssembling, ExpressAssembling]),
        new("mining", "Górnictwo", Items.IronOre,
            [Stonemasonry, BrickFurnaces, Metallurgy, BlastFurnaces, MiningAutomation, Crushing, MechanizedQuarrying, ConeCrushing, Explosives,
             DeepMining, OpenPitMining, OreProcessing, MeteoriteTools, Geology, Coking, PreciousMetals]),
        new("forestry", "Leśnictwo", Items.Wood,
            [ForestryIndustry, Silviculture, Woodworking, IndustrialForestry, Sawing, Harvesters, MeteoriteSaws, Papermaking, Furniture, Charring, FireProtection, Beekeeping, ForestFoods,
             TreeNursery]),
        new("food", "Przetwórstwo", Items.Bread,
            [Milling, OilPressing, Pickling, Baking, RollerMilling, GrainStorage, Cooking, Provisions, FlourIndustry, Confectionery]),
        new("husbandry", "Hodowla", Items.Cow,
            [Breeding, Feeders, Dairying, Distilling, Butchery, Biogas, WorksCanteens, WorkMeals, BioLab, Horsemanship, FineCuisine, Textiles, AutoBarns,
             PigsAndGoats]),
        new("farming", "Rolnictwo", Items.Wheat, [Farmsteads, SugarBeets, Irrigation, FiberCrops, ManorFarms, MechanizedFarming]),
        new("energy", "Energia", Items.WindTurbine,
            [Hydropower, HydroPlant, WindPower, LargeWind, PowerDistribution, EmissionControl, ThermalPower, EnergyStorage, NuclearPower, BiomassPower, Cogeneration,
             GeothermalPower, HydrogenPower]),
        new("oil", "Ropa i gaz", Items.CrudeOil,
            [OilExtraction, Refining, EnhancedRecovery, OffshoreDrilling, Cracking, Petrochemistry, Combustion, Roads]),
        new("coast", "Nabrzeże", Items.Herring, [Fishing, SaltWorks, FishProcessing, Seafaring, Aquaculture, TidalPower]),
        new("desert", "Pustynia", Items.SolarPanel,
            [Glassmaking, WaterSupply, SiliconWorks, BuildingMaterials, SolarPower, SolarTower]),
        new("glacier", "Lodowiec", Items.Ice,
            [Heating, PolarTransport, Refrigeration, Glaciology, PolarTourism, DataCenter]),
        new("city", "Miasta", Items.Shop,
            [Commerce, CityServices, WasteManagement, FreightHub, ExportMarket, Assembly, Appliances, UrbanConstruction, Headquarters, Tailoring]),
        new("materials", "Materiały", Items.CarbonComposite,
            [Lubricant, Paint, CaseinGlue, InsulationBoard, Firebrick, PrecastConcrete, GlassLaminate, Coolant, IridiumAlloy,
             ElectricWire, PaintedSheet, RefractoryLining, CarbonComposite, SuperconductingWire, RocketFuel]),
        new("components", "Podzespoły", Items.Gearbox,
            [MachineShop, Bearing, Coil, WiringHarness, Gearbox, Rotor, MachineHousing, CombustionChamber, CompositePanel,
             SuperconductingMagnet, Alternator, ControlModule, HydraulicActuator, RocketNozzle, PressureVessel, ComputingCore,
             RobotArm, Turbopump, TurbineSet, CryogenicTank, IndustrialRobot, NavigationSystem]),
        new("factories", "Fabryki", Items.Megafactory,
            [RoboticHall, OxygenConverter, RollingMill, ControlCenter, ContinuousCaster, AutomatedWarehouse, SteelCombine, Megafactory]),
        new("space", "Kosmos i fuzja", Items.LaunchRocket,
            [RocketEngine, PlasmaChamber, Avionics, PlasmaControl, RocketStage, Satellite, FusionReactor, LaunchRocket, FusionPlant]),
        new("company", "Spółki", Items.CompanyHq, [Megaprojects, CorporateFinance, Banking]),
    ];

    /// <summary>The branch each technology belongs to.</summary>
    public static readonly IReadOnlyDictionary<string, Branch> BranchOf =
        Branches.SelectMany(b => b.Technologies.Select(t => (t, b))).ToDictionary(p => p.t, p => p.b);

    public static readonly IReadOnlyDictionary<string, Technology> ById = Technologies.ToDictionary(t => t.Id);

    /// <summary>The technology that unlocks each item it lists.</summary>
    public static readonly IReadOnlyDictionary<string, Technology> ByItem =
        Technologies.SelectMany(t => t.Unlocks.Select(item => (item, t))).ToDictionary(p => p.item, p => p.t);

    /// <summary>How deep in the tree a technology lies: 0 for the ones with no requirements. The client draws a column for each.</summary>
    public static int Tier(Technology technology) =>
        technology.Requires.Count == 0 ? 0 : 1 + technology.Requires.Max(r => Tier(ById[r]));
}

/// <summary>A branch of industry and its technologies, roots first.</summary>
/// <param name="Icon">The item whose icon the branch's tab shows.</param>
public sealed record Branch(string Id, string Name, string Icon, IReadOnlyList<string> Technologies);

public enum ResearchStatus { Locked, Available, Active, Done }

/// <summary>The player's research now, after handing out XP for what finished.</summary>
/// <param name="Started">Every technology the player started, done ones included.</param>
/// <param name="Finished">Technologies that finished since the player last asked.</param>
/// <param name="Shared">Technologies the player has because their company researched them.</param>
/// <param name="Company">The company's research, when the player is in a company.</param>
public sealed record ResearchState(
    IReadOnlyDictionary<string, (DateTimeOffset StartsAt, DateTimeOffset FinishesAt)> Started,
    DateTimeOffset ServerTime,
    IReadOnlyList<string> Finished,
    XpGain? Xp,
    IReadOnlySet<string>? Shared = null,
    CompanyResearch? Company = null)
{
    public bool IsDone(string id) => Started.TryGetValue(id, out var r) && r.FinishesAt <= ServerTime;

    public ResearchStatus Status(Technology technology) =>
        IsDone(technology.Id) ? ResearchStatus.Done
        : Started.ContainsKey(technology.Id) ? ResearchStatus.Active
        : technology.Requires.All(IsDone) ? ResearchStatus.Available
        : ResearchStatus.Locked;
}

/// <summary>What a company researches together: one technology at a time, which starts once members brought its whole cost.</summary>
/// <param name="Technology">The one being collected for or researched; null when none.</param>
/// <param name="StartsAt">Null while members are still bringing its cost.</param>
/// <param name="Delivered">How much of each cost item members brought so far.</param>
/// <param name="Done">What the company researched, for every member.</param>
public sealed record CompanyResearch(
    string Name, bool Leads, string? Technology, DateTimeOffset? StartsAt, DateTimeOffset? FinishesAt,
    IReadOnlyDictionary<string, long> Delivered, IReadOnlySet<string> Done);

public sealed class ResearchService(GameDb db, IInventory inventory, IWallet wallet, ICharacterProgress progress, ICompanies companies, TimeProvider time)
    : ITechnologies, ICompanyTechnologies
{
    /// <summary>How long the research takes the player: the orbital station of their company shortens it.</summary>
    private async Task<TimeSpan> DurationAsync(Guid player, Technology technology, CancellationToken cancellationToken) =>
        technology.Time * CompanyPerks.ResearchFactor(await companies.ProjectsAsync(player, cancellationToken));

    public Task<ResearchState> StateAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Starts researching a technology whose requirements are done: its cost is used up now, the unlock comes later.</summary>
    public Task<ResearchState> StartAsync(Guid owner, string id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var technology = TechTree.ById.GetValueOrDefault(id) ?? throw new GameException("no_technology", "There is no such technology.");
            if (technology.CompanyOnly)
            {
                throw new GameException("company_only", "Only a company researches this, together.");
            }

            // One start at a time per player, so two at once cannot both pass the check below.
            await using (var lockOwner = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken))
            {
                lockOwner.Parameters.AddWithValue("key", BitConverter.ToInt64(owner.ToByteArray()) ^ 0x7265736561726368);
                await lockOwner.ExecuteNonQueryAsync(cancellationToken);
            }

            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            var state = await ReadAsync(owner, finished, xp, cancellationToken);
            switch (state.Status(technology))
            {
                case ResearchStatus.Done:
                    throw new GameException("already_researched", "This technology is already researched.");
                case ResearchStatus.Active:
                case ResearchStatus.Available when state.Started.Keys.Any(t => !state.IsDone(t)):
                    throw new GameException("research_busy", "Another technology is being researched; wait for it or cancel it.");
                case ResearchStatus.Locked:
                    throw new GameException("research_locked", "Research the technologies it needs first.");
            }

            foreach (var (item, quantity) in technology.Cost)
            {
                await inventory.RemoveAsync(owner, item, quantity, cancellationToken);
            }

            if (technology.CostCents > 0)
            {
                await wallet.DebitAsync(owner, technology.CostCents, $"research {technology.Id}", cancellationToken);
            }

            var now = time.GetUtcNow();
            await using (var insert = await db.CommandAsync(
                """
                INSERT INTO research.technologies (owner_id, technology, starts_at, finishes_at, rewarded)
                VALUES (@owner, @technology, @start, @end, false)
                """, cancellationToken))
            {
                insert.Parameters.AddWithValue("owner", owner);
                insert.Parameters.AddWithValue("technology", technology.Id);
                insert.Parameters.AddWithValue("start", now);
                insert.Parameters.AddWithValue("end", now + await DurationAsync(owner, technology, cancellationToken));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Speeds the research being done now up: data from a polar station and a server room's computing power each take seconds off it (<see cref="Items.ResearchBoost"/>).</summary>
    public Task<ResearchState> BoostAsync(Guid owner, string item, int quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (!Items.ResearchBoost.TryGetValue(item, out var seconds))
            {
                throw new GameException("not_boost", "Only research data and computing power speed research up.");
            }

            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Put in at least one.");
            }

            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            var now = time.GetUtcNow();
            DateTimeOffset finishes;
            await using (var select = await db.CommandAsync(
                "SELECT finishes_at FROM research.technologies WHERE owner_id = @owner AND finishes_at > @now FOR UPDATE", cancellationToken))
            {
                select.Parameters.AddWithValue("owner", owner);
                select.Parameters.AddWithValue("now", now);
                if (await select.ExecuteScalarAsync(cancellationToken) is not DateTime at)
                {
                    throw new GameException("no_research", "Nothing is being researched.");
                }

                finishes = new DateTimeOffset(at, TimeSpan.Zero);
            }

            // No more is used up than the research still needs.
            var left = finishes - now;
            var used = (int)Math.Min(quantity, Math.Ceiling(left.TotalSeconds / seconds));
            var cut = TimeSpan.FromSeconds((double)used * seconds);
            if (cut > left)
            {
                cut = left;
            }

            await inventory.RemoveAsync(owner, item, used, cancellationToken);
            await using (var update = await db.CommandAsync(
                """
                UPDATE research.technologies SET starts_at = starts_at - @cut, finishes_at = finishes_at - @cut
                WHERE owner_id = @owner AND finishes_at > @now
                """, cancellationToken))
            {
                update.Parameters.AddWithValue("owner", owner);
                update.Parameters.AddWithValue("now", now);
                update.Parameters.AddWithValue("cut", cut);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            var (done, more) = await CompleteDueAsync(owner, cancellationToken);
            return await ReadAsync(owner, [.. finished, .. done], more ?? xp, cancellationToken);
        }, cancellationToken);

    /// <summary>Calls off the research that is not done yet; its cost comes back.</summary>
    public Task<ResearchState> CancelAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            await using (var delete = await db.CommandAsync(
                "DELETE FROM research.technologies WHERE owner_id = @owner AND finishes_at > @now RETURNING technology", cancellationToken))
            {
                delete.Parameters.AddWithValue("owner", owner);
                delete.Parameters.AddWithValue("now", time.GetUtcNow());
                if (await delete.ExecuteScalarAsync(cancellationToken) is not string id)
                {
                    throw new GameException("no_research", "Nothing is being researched.");
                }

                if (TechTree.ById.TryGetValue(id, out var technology))
                {
                    foreach (var (item, quantity) in technology.Cost)
                    {
                        await inventory.AddAsync(owner, item, quantity, cancellationToken);
                    }

                    if (technology.CostCents > 0)
                    {
                        await wallet.CreditAsync(owner, technology.CostCents, $"research {technology.Id} cancelled", cancellationToken);
                    }
                }
            }

            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> LockedAsync(Guid player, CancellationToken cancellationToken)
    {
        var done = await ResearchedAsync(player, cancellationToken);
        return TechTree.ByItem.Where(p => !done.Contains(p.Value.Id)).ToDictionary(p => p.Key, p => p.Value.Name);
    }

    public async Task<IReadOnlySet<string>> ResearchedAsync(Guid player, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT technology FROM research.technologies WHERE owner_id = @owner AND finishes_at <= @now", cancellationToken);
        command.Parameters.AddWithValue("owner", player);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var done = new HashSet<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                done.Add(reader.GetString(0));
            }
        }

        if (await companies.MembershipAsync(player, cancellationToken) is { } membership)
        {
            done.UnionWith(await CompanyDoneAsync(membership.Company, cancellationToken));
        }

        return done;
    }

    public async Task RequireUnlockedAsync(Guid player, string item, CancellationToken cancellationToken)
    {
        if (TechTree.ByItem.ContainsKey(item) && (await LockedAsync(player, cancellationToken)).TryGetValue(item, out var technology))
        {
            throw new GameException("locked", $"Research {technology} first.");
        }
    }

    private async Task<(IReadOnlyList<string> Finished, XpGain? Xp)> CompleteDueAsync(Guid owner, CancellationToken cancellationToken)
    {
        // UPDATE ... RETURNING hands each finished technology's XP to exactly one request.
        await using var command = await db.CommandAsync(
            """
            UPDATE research.technologies SET rewarded = true
            WHERE owner_id = @owner AND NOT rewarded AND finishes_at <= @now
            RETURNING technology
            """, cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var finished = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                finished.Add(reader.GetString(0));
            }
        }

        var xp = finished.Sum(id => TechTree.ById.TryGetValue(id, out var t) ? t.Xp : 0);
        return xp > 0 ? (finished, await progress.AddXpAsync(owner, xp, cancellationToken)) : (finished, null);
    }

    private async Task<ResearchState> ReadAsync(Guid owner, IReadOnlyList<string> finished, XpGain? xp, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT technology, starts_at, finishes_at FROM research.technologies WHERE owner_id = @owner", cancellationToken);
        command.Parameters.AddWithValue("owner", owner);
        var started = new Dictionary<string, (DateTimeOffset, DateTimeOffset)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                started[reader.GetString(0)] = (reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2));
            }
        }

        var now = time.GetUtcNow();
        if (await companies.MembershipAsync(owner, cancellationToken) is not { } membership)
        {
            return new ResearchState(started, now, finished, xp);
        }

        var company = await CompanyStateAsync(membership, cancellationToken);
        var shared = new HashSet<string>();
        foreach (var id in company.Done)
        {
            if (!started.TryGetValue(id, out var own) || own.Item2 > now)
            {
                shared.Add(id);
                started[id] = (now, now);
            }
        }

        return new ResearchState(started, now, finished, xp, shared, company);
    }

    public async Task<bool> ResearchedAsync(Guid company, string technology, CancellationToken cancellationToken) =>
        (await CompanyDoneAsync(company, cancellationToken)).Contains(technology);

    /// <summary>The owner or a partner picks what the company researches next; members then bring its cost (<see cref="ContributeAsync"/>).</summary>
    public Task<ResearchState> StartCompanyAsync(Guid owner, string id, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var technology = TechTree.ById.GetValueOrDefault(id) ?? throw new GameException("no_technology", "There is no such technology.");
            await RequireCompanyAsync(owner, true, cancellationToken);
            var (finished, xp) = await CompleteDueAsync(owner, cancellationToken);
            var state = await ReadAsync(owner, finished, xp, cancellationToken);
            var company = state.Company!;
            if (company.Done.Contains(id))
            {
                throw new GameException("already_researched", "The company has researched this already.");
            }

            if (company.Technology is not null)
            {
                throw new GameException("research_busy", "The company is researching something else; finish or cancel it first.");
            }

            // What it needs counts when the company or this player has it.
            if (!technology.Requires.All(state.IsDone))
            {
                throw new GameException("research_locked", "Research the technologies it needs first.");
            }

            await using (var insert = await db.CommandAsync(
                "INSERT INTO research.company_technologies (company_id, technology) VALUES (@company, @technology)", cancellationToken))
            {
                insert.Parameters.AddWithValue("company", (await companies.MembershipAsync(owner, cancellationToken))!.Company);
                insert.Parameters.AddWithValue("technology", id);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, finished, xp, cancellationToken);
        }, cancellationToken);

    /// <summary>A member brings part of the cost of what the company researches; with the whole cost in, the research starts.</summary>
    public Task<ResearchState> ContributeAsync(Guid owner, string item, long quantity, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            if (quantity < 1)
            {
                throw new GameException("bad_quantity", "Bring at least one.");
            }

            var membership = await RequireCompanyAsync(owner, false, cancellationToken);
            var company = await CompanyStateAsync(membership, cancellationToken);
            if (company.Technology is not { } id || company.StartsAt is not null)
            {
                throw new GameException("no_research", "The company is not collecting for any research.");
            }

            var technology = TechTree.ById[id];
            var needed = Needed(technology, item) - company.Delivered.GetValueOrDefault(item);
            if (needed <= 0)
            {
                throw new GameException("not_needed", "The research needs no more of this.");
            }

            var used = Math.Min(quantity, needed);
            if (item == TechTree.Money)
            {
                await wallet.DebitAsync(owner, used, $"company research {id}", cancellationToken);
            }
            else
            {
                await inventory.RemoveAsync(owner, item, used, cancellationToken);
            }

            await using (var upsert = await db.CommandAsync(
                """
                INSERT INTO research.company_contributions (company_id, technology, item, delivered) VALUES (@company, @technology, @item, @used)
                ON CONFLICT (company_id, technology, item) DO UPDATE SET delivered = company_contributions.delivered + excluded.delivered;
                INSERT INTO research.company_contributors (company_id, technology, account_id, item, delivered) VALUES (@company, @technology, @owner, @item, @used)
                ON CONFLICT (company_id, technology, account_id, item) DO UPDATE SET delivered = company_contributors.delivered + excluded.delivered;
                """, cancellationToken))
            {
                upsert.Parameters.AddWithValue("owner", owner);
                upsert.Parameters.AddWithValue("company", membership.Company);
                upsert.Parameters.AddWithValue("technology", id);
                upsert.Parameters.AddWithValue("item", item);
                upsert.Parameters.AddWithValue("used", used);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            var delivered = new Dictionary<string, long>(company.Delivered) { [item] = company.Delivered.GetValueOrDefault(item) + used };
            if (technology.Cost.All(c => delivered.GetValueOrDefault(c.Item) >= c.Quantity) && delivered.GetValueOrDefault(TechTree.Money) >= technology.CostCents)
            {
                var now = time.GetUtcNow();
                await using var start = await db.CommandAsync(
                    "UPDATE research.company_technologies SET starts_at = @start, finishes_at = @end WHERE company_id = @company AND technology = @technology",
                    cancellationToken);
                start.Parameters.AddWithValue("company", membership.Company);
                start.Parameters.AddWithValue("technology", id);
                start.Parameters.AddWithValue("start", now);
                start.Parameters.AddWithValue("end", now + await DurationAsync(owner, technology, cancellationToken));
                await start.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, [], null, cancellationToken);
        }, cancellationToken);

    /// <summary>The owner or a partner calls off the company's research that is not done; what each member brought goes back to them
    /// (what was brought before members were counted goes to the one who calls it off).</summary>
    public Task<ResearchState> CancelCompanyAsync(Guid owner, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            var membership = await RequireCompanyAsync(owner, true, cancellationToken);
            var company = await CompanyStateAsync(membership, cancellationToken);
            if (company.Technology is not { } id)
            {
                throw new GameException("no_research", "The company is not researching anything.");
            }

            var back = new List<(Guid Account, string Item, long Quantity)>();
            await using (var select = await db.CommandAsync(
                "SELECT account_id, item, delivered FROM research.company_contributors WHERE company_id = @company AND technology = @technology AND delivered > 0",
                cancellationToken))
            {
                select.Parameters.AddWithValue("company", membership.Company);
                select.Parameters.AddWithValue("technology", id);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    back.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            foreach (var (item, quantity) in company.Delivered.Where(d => d.Value > 0))
            {
                var unclaimed = quantity - back.Where(b => b.Item == item).Sum(b => b.Quantity);
                if (unclaimed > 0)
                {
                    back.Add((owner, item, unclaimed));
                }
            }

            foreach (var (account, item, quantity) in back)
            {
                if (item == TechTree.Money)
                {
                    await wallet.CreditAsync(account, quantity, $"company research {id} cancelled", cancellationToken);
                }
                else
                {
                    await inventory.AddAsync(account, item, quantity, cancellationToken);
                }
            }

            await using (var delete = await db.CommandAsync(
                """
                DELETE FROM research.company_contributors WHERE company_id = @company AND technology = @technology;
                DELETE FROM research.company_contributions WHERE company_id = @company AND technology = @technology;
                DELETE FROM research.company_technologies WHERE company_id = @company AND technology = @technology;
                """, cancellationToken))
            {
                delete.Parameters.AddWithValue("company", membership.Company);
                delete.Parameters.AddWithValue("technology", id);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            return await ReadAsync(owner, [], null, cancellationToken);
        }, cancellationToken);

    /// <summary>How much of an item, or of <see cref="TechTree.Money"/> in cents, the research uses up.</summary>
    private static long Needed(Technology technology, string item) =>
        item == TechTree.Money ? technology.CostCents : technology.Cost.Where(c => c.Item == item).Sum(c => (long)c.Quantity);

    /// <summary>The player's company, locked so its research changes one request at a time.</summary>
    private async Task<CompanyMembership> RequireCompanyAsync(Guid player, bool leads, CancellationToken cancellationToken)
    {
        var membership = await companies.MembershipAsync(player, cancellationToken)
            ?? throw new GameException("no_company", "You are not in a company.");
        if (leads && !membership.Leads)
        {
            throw new GameException("not_allowed", "Only the owner and partners choose the company's research.");
        }

        await using var lockCompany = await db.CommandAsync("SELECT pg_advisory_xact_lock(@key)", cancellationToken);
        lockCompany.Parameters.AddWithValue("key", BitConverter.ToInt64(membership.Company.ToByteArray()) ^ 0x636f6d70616e79);
        await lockCompany.ExecuteNonQueryAsync(cancellationToken);
        return membership;
    }

    private async Task<HashSet<string>> CompanyDoneAsync(Guid company, CancellationToken cancellationToken)
    {
        await using var command = await db.CommandAsync(
            "SELECT technology FROM research.company_technologies WHERE company_id = @company AND finishes_at <= @now", cancellationToken);
        command.Parameters.AddWithValue("company", company);
        command.Parameters.AddWithValue("now", time.GetUtcNow());
        var done = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            done.Add(reader.GetString(0));
        }

        return done;
    }

    private async Task<CompanyResearch> CompanyStateAsync(CompanyMembership membership, CancellationToken cancellationToken)
    {
        string? current = null;
        DateTimeOffset? startsAt = null, finishesAt = null;
        await using (var command = await db.CommandAsync(
            "SELECT technology, starts_at, finishes_at FROM research.company_technologies WHERE company_id = @company AND (finishes_at IS NULL OR finishes_at > @now)",
            cancellationToken))
        {
            command.Parameters.AddWithValue("company", membership.Company);
            command.Parameters.AddWithValue("now", time.GetUtcNow());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                current = reader.GetString(0);
                startsAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
                finishesAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
            }
        }

        var delivered = new Dictionary<string, long>();
        if (current is not null)
        {
            await using var command = await db.CommandAsync(
                "SELECT item, delivered FROM research.company_contributions WHERE company_id = @company AND technology = @technology", cancellationToken);
            command.Parameters.AddWithValue("company", membership.Company);
            command.Parameters.AddWithValue("technology", current);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                delivered[reader.GetString(0)] = reader.GetInt64(1);
            }
        }

        return new CompanyResearch(membership.Name, membership.Leads, current, startsAt, finishesAt, delivered,
            await CompanyDoneAsync(membership.Company, cancellationToken));
    }
}

public sealed record StartResearchRequest(string Technology);

public abstract class ResearchHandler(IServiceScopeFactory scopes) : IMessageHandler
{
    public abstract string Type { get; }

    public async Task<Reply> HandleAsync(JsonElement? payload, ClientContext client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var state = await RunAsync(scope.ServiceProvider.GetRequiredService<ResearchService>(), client.RequireAccount(), payload, cancellationToken);
        return new Reply("research", new
        {
            // Unix milliseconds: the client compares them with serverTime, not its own clock.
            technologies = TechTree.Technologies.Select(t => new
            {
                id = t.Id,
                name = t.Name,
                description = t.Description,
                tier = TechTree.Tier(t),
                branch = TechTree.BranchOf[t.Id].Id,
                requires = t.Requires,
                cost = t.Cost.Select(c => new { id = c.Item, name = Items.Names[c.Item], quantity = c.Quantity }),
                // Tiles of the inventory the cost takes: it is all used up at once, so it must all fit in the inventory.
                costTiles = t.Cost.Sum(c => Stacks.Tiles(c.Item, c.Quantity)),
                // Money it uses up besides the items.
                costCents = t.CostCents,
                seconds = t.Time.TotalSeconds,
                xp = t.Xp,
                unlocks = t.Unlocks.Select(u => new { id = u, name = Items.Names[u] }),
                // The item whose icon it shows when it unlocks none, or null.
                icon = t.Icon,
                status = state.Status(t) switch
                {
                    ResearchStatus.Done => "done",
                    ResearchStatus.Active => "active",
                    ResearchStatus.Available => "available",
                    _ => "locked",
                },
                startsAt = state.Started.TryGetValue(t.Id, out var r) ? r.StartsAt.ToUnixTimeMilliseconds() : (long?)null,
                finishesAt = state.Started.TryGetValue(t.Id, out var f) ? f.FinishesAt.ToUnixTimeMilliseconds() : (long?)null,
                companyOnly = t.CompanyOnly,
                // Researched by the player's company, not by them.
                shared = state.Shared?.Contains(t.Id) == true,
            }),
            company = state.Company is { } c ? new
            {
                name = c.Name,
                leads = c.Leads,
                technology = c.Technology,
                startsAt = c.StartsAt?.ToUnixTimeMilliseconds(),
                finishesAt = c.FinishesAt?.ToUnixTimeMilliseconds(),
                delivered = c.Technology is { } id
                    ? TechTree.ById[id].Cost.Select(p => new { id = p.Item, name = Items.Names[p.Item], needed = (long)p.Quantity, delivered = c.Delivered.GetValueOrDefault(p.Item) })
                        // Money in cents, under the id "money".
                        .Concat(TechTree.ById[id].CostCents > 0
                            ? [new { id = TechTree.Money, name = "Pieniądze", needed = TechTree.ById[id].CostCents, delivered = c.Delivered.GetValueOrDefault(TechTree.Money) }]
                            : [])
                    : null,
            } : null,
            // What speeds research up, and by how many seconds each piece.
            boosts = Items.ResearchBoost.Select(b => new { id = b.Key, name = Items.Names[b.Key], seconds = b.Value }),
            branches = TechTree.Branches.Select(b => new { id = b.Id, name = b.Name, icon = b.Icon }),
            serverTime = state.ServerTime.ToUnixTimeMilliseconds(),
            finished = state.Finished.Where(TechTree.ById.ContainsKey).Select(id => new { id, name = TechTree.ById[id].Name }),
            xp = state.Xp is { } xp ? new { xp = xp.Xp, level = xp.Level, leveledUp = xp.LeveledUp } : null,
        });
    }

    protected abstract Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken);
}

/// <summary>The tree and the player's research; also hands out XP for finished research, so the client calls it when a timer runs out.</summary>
public sealed class GetResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.get";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.StateAsync(owner, cancellationToken);
}

public sealed class StartResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.start";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.StartAsync(owner, Payload.Read<StartResearchRequest>(payload).Technology ?? "", cancellationToken);
}

public sealed record BoostResearchRequest(string Item, int Quantity);

/// <summary>Speeds the research being done up with research data or computing power.</summary>
public sealed class BoostResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.boost";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<BoostResearchRequest>(payload);
        return research.BoostAsync(owner, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

public sealed class CancelResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.cancel";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.CancelAsync(owner, cancellationToken);
}

/// <summary>The company picks what it researches together.</summary>
public sealed class StartCompanyResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.companyStart";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.StartCompanyAsync(owner, Payload.Read<StartResearchRequest>(payload).Technology ?? "", cancellationToken);
}

public sealed record ContributeRequest(string Item, long Quantity);

/// <summary>A member brings part of the cost of the company's research.</summary>
public sealed class ContributeResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.contribute";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken)
    {
        var request = Payload.Read<ContributeRequest>(payload);
        return research.ContributeAsync(owner, request.Item ?? "", request.Quantity, cancellationToken);
    }
}

public sealed class CancelCompanyResearchHandler(IServiceScopeFactory scopes) : ResearchHandler(scopes)
{
    public override string Type => "research.companyCancel";

    protected override Task<ResearchState> RunAsync(ResearchService research, Guid owner, JsonElement? payload, CancellationToken cancellationToken) =>
        research.CancelCompanyAsync(owner, cancellationToken);
}
