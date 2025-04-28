using MelonLoader;
using System.Reflection;
using HarmonyLib;
using ScheduleOne.Employees;
using ScheduleOne.NPCs.CharacterClasses;
using Guid = System.Guid;

namespace NoLazyWorkers.Settings
{
  [Serializable]
  public class BaseEmployee
  {
    public string SigningFee { get; set; }
    public string DailyWage { get; set; }
    public string WalkSpeed { get; set; }
    public string MaxHealth { get; set; }
  }

  [Serializable]
  public class BotanistSettings : BaseEmployee
  {
    public string SoilPourTime { get; set; }
    public string SeedSowTime { get; set; }
    public string AdditivePourTime { get; set; }
    public string WaterPourTime { get; set; }
    public string WateringCriticalThreshold { get; set; }
    public string WateringThreshold { get; set; }
    public string WateringTargetMin { get; set; }
    public string WateringTargetMax { get; set; }
    public string HarvestTime { get; set; }
  }

  [Serializable]
  public class ChemistSettings : BaseEmployee
  {
    public string MixingPerIngredientTime { get; set; }
    public string PlaceIngredientsTime { get; set; }
    public string StirTime { get; set; }
    public string BurnerTime { get; set; }
  }

  [Serializable]
  public class CleanerSettings : BaseEmployee
  {
  }

  [Serializable]
  public class PackagerSettings : BaseEmployee
  {
    public string PackagingSpeedMultiplier { get; set; }
  }

  [Serializable]
  public class FixerSettings : BaseEmployee
  {
    public string AdditionalSigningFee1 { get; set; }
    public string AdditionalSigningFee2 { get; set; }
    public string MaxAdditionalFee { get; set; }
    public string AdditionalFeeThreshold { get; set; }
  }

  [Serializable]
  public class MiscSettings
  {
    public string StoreDeliveryFee { get; set; }
  }

  [Serializable]
  public class Default
  {
    public BotanistSettings Botanist { get; set; } = new BotanistSettings();
    public ChemistSettings Chemist { get; set; } = new ChemistSettings();
    public CleanerSettings Cleaner { get; set; } = new CleanerSettings();
    public PackagerSettings Packager { get; set; } = new PackagerSettings();
    public FixerSettings Fixer { get; set; } = new FixerSettings();
    public MiscSettings Misc { get; set; } = new MiscSettings();

    public static Default LoadFromFile(string filePath)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: LoadFromFile");
      var settings = new Default();
      string currentSection = "";
      foreach (string line in File.ReadAllLines(filePath))
      {
        string trimmedLine = line.Trim();
        if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("//"))
          continue;

        if (trimmedLine.StartsWith("#"))
        {
          currentSection = trimmedLine.Substring(1).ToLower();
          continue;
        }

        var parts = trimmedLine.Split('=').Select(p => p.Trim()).ToArray();
        if (parts.Length != 2)
          continue;

        string key = parts[0];
        string value = parts[1];

        switch (currentSection)
        {
          case "botanist":
            SetProperty(settings.Botanist, key, value);
            break;
          case "chemist":
            SetProperty(settings.Chemist, key, value);
            break;
          case "cleaner":
            SetProperty(settings.Cleaner, key, value);
            break;
          case "packager":
            SetProperty(settings.Packager, key, value);
            break;
          case "fixer":
            SetProperty(settings.Fixer, key, value);
            break;
          case "misc":
            SetProperty(settings.Misc, key, value);
            break;
        }
      }
      return settings;
    }

    private static void SetProperty(object target, string key, string value)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: SetProperty");
      var property = target.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
      if (property == null || property.PropertyType != typeof(string))
        return;

      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: SetProperty {property} | {target}");
      property.SetValue(target, value);
    }

    public void SaveToFile(string filePath)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: SaveToFile");
      var lines = new List<string>
            {
                "#botanist",
                $"SigningFee = {Botanist.SigningFee}",
                $"DailyWage = {Botanist.DailyWage}",
                $"WalkSpeed = {Botanist.WalkSpeed}",
                $"MaxHealth = {Botanist.MaxHealth}",
                $"SoilPourTime = {Botanist.SoilPourTime}",
                $"SeedSowTime = {Botanist.SeedSowTime}",
                $"AdditivePourTime = {Botanist.AdditivePourTime}",
                $"WaterPourTime = {Botanist.WaterPourTime}",
                $"WateringCriticalThreshold = {Botanist.WateringCriticalThreshold}",
                $"WateringThreshold = {Botanist.WateringThreshold}",
                $"WateringTargetMin = {Botanist.WateringTargetMin}",
                $"WateringTargetMax = {Botanist.WateringTargetMax}",
                $"HarvestTime = {Botanist.HarvestTime}",
                "",
                "#chemist",
                $"SigningFee = {Chemist.SigningFee}",
                $"DailyWage = {Chemist.DailyWage}",
                $"WalkSpeed = {Chemist.WalkSpeed}",
                $"MaxHealth = {Chemist.MaxHealth}",
                $"#MixingPerIngredientTime = {Chemist.MixingPerIngredientTime} #not implemented yet",
                $"#PlaceIngredientsTime = {Chemist.PlaceIngredientsTime} #not implemented yet",
                $"#StirTime = {Chemist.StirTime} #not implemented yet",
                $"#BurnerTime = {Chemist.BurnerTime} #not implemented yet",
                "",
                "#cleaner",
                $"SigningFee = {Cleaner.SigningFee}",
                $"DailyWage = {Cleaner.DailyWage}",
                $"WalkSpeed = {Cleaner.WalkSpeed}",
                $"MaxHealth = {Cleaner.MaxHealth}",
                "",
                "#packager",
                $"SigningFee = {Packager.SigningFee}",
                $"DailyWage = {Packager.DailyWage}",
                $"WalkSpeed = {Packager.WalkSpeed}",
                $"MaxHealth = {Packager.MaxHealth}",
                $"PackagingSpeedMultiplier = {Packager.PackagingSpeedMultiplier}",
                "",
                "#fixer",
                $"#AdditionalSigningFee1 = {Fixer.AdditionalSigningFee1} #not implemented yet",
                $"#AdditionalSigningFee2 = {Fixer.AdditionalSigningFee2} #not implemented yet",
                $"#MaxAdditionalFee = {Fixer.MaxAdditionalFee} #not implemented yet",
                $"#AdditionalFeeThreshold = {Fixer.AdditionalFeeThreshold} #not implemented yet",
                "",
                "#misc",
                $"#StoreDeliveryFee = {Misc.StoreDeliveryFee} #not implemented yet"
            };
      File.WriteAllLines(filePath, lines);
    }

    public Default()
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: Default");
      Botanist = new BotanistSettings
      {
        SigningFee = "1000",
        DailyWage = "200",
        WalkSpeed = "1.2",
        MaxHealth = "100",
        SoilPourTime = "10",
        SeedSowTime = "15",
        AdditivePourTime = "10",
        WaterPourTime = "10",
        WateringCriticalThreshold = "0.2",
        WateringThreshold = "0.3",
        WateringTargetMin = "0.75",
        WateringTargetMax = "1",
        HarvestTime = "15"
      };
      Chemist = new ChemistSettings
      {
        SigningFee = "1500",
        DailyWage = "300",
        WalkSpeed = "1.2",
        MaxHealth = "100",
        MixingPerIngredientTime = "1",
        PlaceIngredientsTime = "8",
        StirTime = "6",
        BurnerTime = "6"
      };
      Cleaner = new CleanerSettings
      {
        SigningFee = "500",
        DailyWage = "100",
        WalkSpeed = "1.2",
        MaxHealth = "100"
      };
      Packager = new PackagerSettings
      {
        SigningFee = "1000",
        DailyWage = "200",
        WalkSpeed = "1.2",
        MaxHealth = "100",
        PackagingSpeedMultiplier = "2"
      };
      Fixer = new FixerSettings
      {
        AdditionalSigningFee1 = "100",
        AdditionalSigningFee2 = "250",
        MaxAdditionalFee = "500",
        AdditionalFeeThreshold = "5"
      };
      Misc = new MiscSettings
      {
        StoreDeliveryFee = "200"
      };
    }
  }

  public class Configure
  {
    private readonly Default config;

    public Configure()
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: Configure");
      config = NoLazyWorkersMod.Instance.Config;
    }

    public void StartCoroutine(Employee employee)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: StartCoroutine {employee.name}");
      MelonCoroutines.Start(ConfigureRoutine(employee));
    }

    private System.Collections.IEnumerator ConfigureRoutine(Employee employee)
    {
      // Wait until the end of the frame to ensure NPC is fully initialized
      yield return null;
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: ConfigureRoutine {employee.name}");

      ApplySettings(employee);
    }

    public void ApplySettings(Employee employee)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: ApplySettings {employee.name}");
      switch (employee)
      {
        case Botanist botanist:
          ApplyBotanistSettings(botanist);
          break;
        case Chemist chemist:
          ApplyChemistSettings(chemist);
          break;
        case Cleaner cleaner:
          ApplyCleanerSettings(cleaner);
          break;
        case Packager packager:
          ApplyPackagerSettings(packager);
          break;
      }
    }

    private void ApplyBotanistSettings(Botanist botanist)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: ApplyBotanistSettings {botanist.fullName}");
      if (int.TryParse(config.Botanist.SigningFee, out int signingFee))
        botanist.SigningFee = signingFee;
      else
        MelonLogger.Error($"Invalid SigningFee for Botanist: {config.Botanist.SigningFee}");
      if (int.TryParse(config.Botanist.DailyWage, out int dailyWage))
        botanist.DailyWage = dailyWage;
      else
        MelonLogger.Error($"Invalid DailyWage for Botanist: {config.Botanist.DailyWage}");
      if (float.TryParse(config.Botanist.WalkSpeed, out float walkSpeed))
        botanist.movement.WalkSpeed = walkSpeed;
      else
        MelonLogger.Error($"Invalid WalkSpeed for Botanist: {config.Botanist.WalkSpeed}");
      if (float.TryParse(config.Botanist.MaxHealth, out float maxHealth))
        botanist.Health.MaxHealth = maxHealth;
      else
        MelonLogger.Error($"Invalid MaxHealth for Botanist: {config.Botanist.MaxHealth}");
      if (float.TryParse(config.Botanist.SoilPourTime, out float soilPourTime))
        botanist.SOIL_POUR_TIME = soilPourTime;
      else
        MelonLogger.Error($"Invalid SoilPourTime for Botanist: {config.Botanist.SoilPourTime}");
      if (float.TryParse(config.Botanist.SeedSowTime, out float seedSowTime))
        botanist.SEED_SOW_TIME = seedSowTime;
      else
        MelonLogger.Error($"Invalid SeedSowTime for Botanist: {config.Botanist.SeedSowTime}");
      if (float.TryParse(config.Botanist.AdditivePourTime, out float additivePourTime))
        botanist.ADDITIVE_POUR_TIME = additivePourTime;
      else
        MelonLogger.Error($"Invalid AdditivePourTime for Botanist: {config.Botanist.AdditivePourTime}");
      if (float.TryParse(config.Botanist.WateringCriticalThreshold, out float wateringCriticalThreshold))
        botanist.CRITICAL_WATERING_THRESHOLD = wateringCriticalThreshold;
      else
        MelonLogger.Error($"Invalid WateringCriticalThreshold for Botanist: {config.Botanist.WateringCriticalThreshold}");
      if (float.TryParse(config.Botanist.WateringThreshold, out float wateringThreshold))
        botanist.WATERING_THRESHOLD = wateringThreshold;
      else
        MelonLogger.Error($"Invalid WateringThreshold for Botanist: {config.Botanist.WateringThreshold}");
      if (float.TryParse(config.Botanist.WateringTargetMin, out float wateringTargetMin))
        botanist.TARGET_WATER_LEVEL_MIN = wateringTargetMin;
      else
        MelonLogger.Error($"Invalid WateringTargetMin for Botanist: {config.Botanist.WateringTargetMin}");
      if (float.TryParse(config.Botanist.WateringTargetMax, out float wateringTargetMax))
        botanist.TARGET_WATER_LEVEL_MAX = wateringTargetMax;
      else
        MelonLogger.Error($"Invalid WateringTargetMax for Botanist: {config.Botanist.WateringTargetMax}");
      if (float.TryParse(config.Botanist.WaterPourTime, out float waterPourTime))
        botanist.WATER_POUR_TIME = waterPourTime;
      else
        MelonLogger.Error($"Invalid WaterPourTime for Botanist: {config.Botanist.WaterPourTime}");
      if (float.TryParse(config.Botanist.HarvestTime, out float harvestTime))
        botanist.HARVEST_TIME = harvestTime;
      else
        MelonLogger.Error($"Invalid HarvestTime for Botanist: {config.Botanist.HarvestTime}");
    }

    private void ApplyChemistSettings(Chemist chemist)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: ApplyChemistSettings {chemist.fullName}");
      if (int.TryParse(config.Chemist.SigningFee, out int signingFee))
        chemist.SigningFee = signingFee;
      else
        MelonLogger.Error($"Invalid SigningFee for Chemist: {config.Chemist.SigningFee}");
      if (int.TryParse(config.Chemist.DailyWage, out int dailyWage))
        chemist.DailyWage = dailyWage;
      else
        MelonLogger.Error($"Invalid DailyWage for Chemist: {config.Chemist.DailyWage}");
      if (float.TryParse(config.Chemist.WalkSpeed, out float walkSpeed))
        chemist.movement.WalkSpeed = walkSpeed;
      else
        MelonLogger.Error($"Invalid WalkSpeed for Chemist: {config.Chemist.WalkSpeed}");
      if (float.TryParse(config.Chemist.MaxHealth, out float maxHealth))
        chemist.Health.MaxHealth = maxHealth;
      else
        MelonLogger.Error($"Invalid MaxHealth for Chemist: {config.Chemist.MaxHealth}");
      // Uncomment when implemented
      //if (float.TryParse(config.Chemist.MixingPerIngredientTime, out float mixingTime))
      //    StartMixingStationBehaviour.INSERT_INGREDIENT_BASE_TIME = mixingTime;
      //else
      //    MelonLogger.Error($"Invalid MixingPerIngredientTime for Chemist: {config.Chemist.MixingPerIngredientTime}");
      //if (float.TryParse(config.Chemist.PlaceIngredientsTime, out float placeTime))
      //    StartChemistryStationBehaviour.PLACE_INGREDIENTS_TIME = placeTime;
      //else
      //    MelonLogger.Error($"Invalid PlaceIngredientsTime for Chemist: {config.Chemist.PlaceIngredientsTime}");
      //if (float.TryParse(config.Chemist.StirTime, out float stirTime))
      //    StartChemistryStationBehaviour.STIR_TIME = stirTime;
      //else
      //    MelonLogger.Error($"Invalid StirTime for Chemist: {config.Chemist.StirTime}");
      //if (float.TryParse(config.Chemist.BurnerTime, out float burnerTime))
      //    StartChemistryStationBehaviour.BURNER_TIME = burnerTime;
      //else
      //    MelonLogger.Error($"Invalid BurnerTime for Chemist: {config.Chemist.BurnerTime}");
    }

    private void ApplyCleanerSettings(Cleaner cleaner)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: ApplyCleanerSettings {cleaner.fullName}");
      if (int.TryParse(config.Cleaner.SigningFee, out int signingFee))
        cleaner.SigningFee = signingFee;
      else
        MelonLogger.Error($"Invalid SigningFee for Cleaner: {config.Cleaner.SigningFee}");
      if (int.TryParse(config.Cleaner.DailyWage, out int dailyWage))
        cleaner.DailyWage = dailyWage;
      else
        MelonLogger.Error($"Invalid DailyWage for Cleaner: {config.Cleaner.DailyWage}");
      if (float.TryParse(config.Cleaner.WalkSpeed, out float walkSpeed))
        cleaner.movement.WalkSpeed = walkSpeed;
      else
        MelonLogger.Error($"Invalid WalkSpeed for Cleaner: {config.Cleaner.WalkSpeed}");
      if (float.TryParse(config.Cleaner.MaxHealth, out float maxHealth))
        cleaner.Health.MaxHealth = maxHealth;
      else
        MelonLogger.Error($"Invalid MaxHealth for Cleaner: {config.Cleaner.MaxHealth}");
    }

    private void ApplyPackagerSettings(Packager packager)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg($"Settings: ApplyPackagerSettings {packager.fullName}");
      if (int.TryParse(config.Packager.SigningFee, out int signingFee))
        packager.SigningFee = signingFee;
      else
        MelonLogger.Error($"Invalid SigningFee for Packager: {config.Packager.SigningFee}");
      if (int.TryParse(config.Packager.DailyWage, out int dailyWage))
        packager.DailyWage = dailyWage;
      else
        MelonLogger.Error($"Invalid DailyWage for Packager: {config.Packager.DailyWage}");
      if (float.TryParse(config.Packager.WalkSpeed, out float walkSpeed))
        packager.movement.WalkSpeed = walkSpeed;
      else
        MelonLogger.Error($"Invalid WalkSpeed for Packager: {config.Packager.WalkSpeed}");
      if (float.TryParse(config.Packager.MaxHealth, out float maxHealth))
        packager.Health.MaxHealth = maxHealth;
      else
        MelonLogger.Error($"Invalid MaxHealth for Packager: {config.Packager.MaxHealth}");
      if (float.TryParse(config.Packager.PackagingSpeedMultiplier, out float packagingSpeed))
        packager.PackagingSpeedMultiplier = packagingSpeed;
      else
        MelonLogger.Error($"Invalid PackagingSpeedMultiplier for Packager: {config.Packager.PackagingSpeedMultiplier}");
    }

    public void ApplyFixerSettings()
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: ApplyFixerSettings");
      /*       if (int.TryParse(config.Fixer.AdditionalSigningFee1, out int signingFee1))
              Fixer.ADDITIONAL_SIGNING_FEE_1 = signingFee1;
            else
              MelonLogger.Error($"Invalid AdditionalSigningFee1 for Fixer: {config.Fixer.AdditionalSigningFee1}");
            if (int.TryParse(config.Fixer.AdditionalSigningFee2, out int signingFee2))
              Fixer.ADDITIONAL_SIGNING_FEE_2 = signingFee2;
            else
              MelonLogger.Error($"Invalid AdditionalSigningFee2 for Fixer: {config.Fixer.AdditionalSigningFee2}");
            if (int.TryParse(config.Fixer.MaxAdditionalFee, out int maxAdditionalFee))
              Fixer.MAX_SIGNING_FEE = maxAdditionalFee;
            else
              MelonLogger.Error($"Invalid MaxAdditionalFee for Fixer: {config.Fixer.MaxAdditionalFee}");
            if (int.TryParse(config.Fixer.AdditionalFeeThreshold, out int feeThreshold))
              Fixer.ADDITIONAL_FEE_THRESHOLD = feeThreshold;
            else
              MelonLogger.Error($"Invalid AdditionalFeeThreshold for Fixer: {config.Fixer.AdditionalFeeThreshold}"); */
    }

    public void ApplyMiscSettings()
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: ApplyMiscSettings");
      if (float.TryParse(config.Misc.StoreDeliveryFee, out float storeDeliveryFee))
      {
        // Apply StoreDeliveryFee to the appropriate game system
        // Example: GameSettings.STORE_DELIVERY_FEE = storeDeliveryFee;
        // Replace with actual game system reference
      }
      else
      {
        MelonLogger.Error($"Invalid StoreDeliveryFee: {config.Misc.StoreDeliveryFee}");
      }
    }
  }

  [HarmonyPatch]
  public static class EmployeePatches
  {
    private static readonly List<Guid> Configured = [];

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Chemist), "Awake")]
    public static void ChemistAwakePostfix(Chemist __instance)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: ChemistAwakePostfix");
      ConfigureEmployee(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Packager), "NetworkInitialize___Early")]
    public static void PackagerAwakePostfix(Packager __instance)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: PackagerAwakePostfix");
      ConfigureEmployee(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Botanist), "Start")]
    public static void BotanistAwakePostfix(Botanist __instance)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: BotanistAwakePostfix");
      ConfigureEmployee(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Cleaner), "NetworkInitialize___Early")]
    public static void CleanerAwakePostfix(Cleaner __instance)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: CleanerAwakePostfix");
      ConfigureEmployee(__instance);
    }

    private static void ConfigureEmployee(Employee employee)
    {
      if (DebugConfig.EnableDebugLogs || DebugConfig.EnableDebugSettings)
        MelonLogger.Msg("Settings: CleanerAwakePostfix");
      if (!Configured.Contains(employee.GUID))
      {
        new Configure().StartCoroutine(employee);
        Configured.Add(employee.GUID);
      }
    }
  }
}