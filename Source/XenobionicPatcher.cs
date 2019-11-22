﻿using RimWorld;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Harmony;
using HugsLib;
using HugsLib.Settings;
using Verse;

namespace XenobionicPatcher {
    [StaticConstructorOnStartup]
    public class Base : ModBase {
        public override string ModIdentifier {
            get { return "XenobionicPatcher"; }
        }
        public static Base         Instance    { get; private set; }
        public static DefInjectors DefInjector { get; private set; }
        public static bool IsDebug             { get; private set; }

        internal HugsLib.Utils.ModLogger ModLogger { get; private set; }

        public Base() {
            Instance    = this;
            DefInjector = new XenobionicPatcher.DefInjectors();
            ModLogger   = this.Logger;
            IsDebug     = false;
        }

        internal Dictionary<string, SettingHandle> config = new Dictionary<string, SettingHandle>();

        internal List<Type> surgeryWorkerClassesFilter = new List<Type> {};

        public override void DefsLoaded() {
            ProcessSettings();

            // Curate the surgery worker class list before building allSurgeryDefs
            Dictionary<string, Type[]> searchConfigMapper = new Dictionary<string, Type[]> {
                { "Adminster",                 new[] { typeof(Recipe_AdministerIngestible), typeof(Recipe_AdministerUsableItem) } },
                { "InstallNaturalBodyPart",    new[] { typeof(Recipe_InstallNaturalBodyPart)    } },
                { "InstallArtificialBodyPart", new[] { typeof(Recipe_InstallArtificialBodyPart) } },
                { "InstallImplant",            new[] { typeof(Recipe_InstallImplant)            } },
                { "VanillaRemoval",            new[] { typeof(Recipe_RemoveHediff), AccessTools.TypeByName("RimWorld.Recipe_RemoveBodyPart") } }, 
            };
            foreach (string cName in searchConfigMapper.Keys) {
                if ( ((SettingHandle<bool>)config["Search" + cName + "Recipes"]).Value ) surgeryWorkerClassesFilter.AddRange( searchConfigMapper[cName] );
            }

            // Add additional search types for modded surgery classes
            if ( ((SettingHandle<bool>)config["SearchModdedSurgeryClasses"]).Value ) {
                List<string> moddedWorkerClassNames = new List<string> {
                    // (EPOE doesn't have any custom worker classes)
                    
                    // Rah's Bionics and Surgery Expansion
                    "ScarRemoving.Recipe_RemoveHediff_noBrain",
                
                    // Medical Surgery Expansion
                    "OrenoMSE.Recipe_InstallBodyPartModule",
                    "OrenoMSE.Recipe_InstallImplantSystem",
                    "OrenoMSE.Recipe_RemoveImplantSystem",

                    // Chj's Androids
                    "Androids.Recipe_Disassemble",
                    "Androids.Recipe_RepairKit",

                    // Android Tiers
                    "MOARANDROIDS.Recipe_InstallImplantAndroid",
                    "MOARANDROIDS.Recipe_InstallArtificialBodyPartAndroid",
                    
                    // Alien vs. Predator
                    "RRYautja.Recipe_Remove_Gauntlet",
                    "RRYautja.Recipe_RemoveHugger",
                };

                foreach (string workerName in moddedWorkerClassNames) {
                    Type worker = Helpers.SafeTypeByName(workerName);

                    if (worker != null) surgeryWorkerClassesFilter.Add(worker);
                }
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            string beforeMsg = "Injecting {0} surgical recipes into {1}";
            string  afterMsg = "Injected {0} surgical recipes into {1} (took {2:F4}s; {3:N0} combinations)";
            
            // Start with a few global lists
            List<ThingDef> allPawnDefs = DefDatabase<ThingDef>.AllDefs.Where(
                thing => Helpers.IsSupertypeOf(typeof(Pawn), thing.thingClass)
            ).ToList();
            List<RecipeDef> allSurgeryDefs = DefDatabase<RecipeDef>.AllDefs.Where(
                recipe => recipe.IsSurgery && surgeryWorkerClassesFilter.Any( t => Helpers.IsSupertypeOf(t, recipe.workerClass) )
            ).ToList();

            // Because we use pawn.recipes so often for surgery checks, and not the other side (surgery.recipeUsers),
            // merge the latter into the former.  Our new additions will be sure to add it in both sides to keep
            // pawn.recipes complete.
            stopwatch.Start();
            foreach (ThingDef pawn in allPawnDefs) {
                if (pawn.recipes == null) pawn.recipes = new List<RecipeDef> {};
                pawn.recipes.AddRange(
                    allSurgeryDefs.Where(s => s.recipeUsers != null && s.recipeUsers.Contains(pawn))
                );
                pawn.recipes.RemoveDuplicates();
            }            

            // Pre-caching
            allSurgeryDefs.ForEach(s => Helpers.GetSurgeryBioType(s));
            allPawnDefs   .ForEach(p => Helpers.GetPawnBioType   (p));
            stopwatch.Stop();

            Logger.Message("Prep work / pre-caching (took {0:F4}s; {1:N0} defs)", stopwatch.ElapsedMilliseconds / 1000f, allSurgeryDefs.Count() + allPawnDefs.Count());

            // Animal/Animal
            if ( ((SettingHandle<bool>)config["PatchAnimalToAnimal"]).Value ) {
                if (IsDebug) Logger.Message(beforeMsg, "animal", "other animals");

                var surgeryList = allSurgeryDefs.Where(s => Helpers.GetSurgeryBioType(s) == "animal");
                var    pawnList = allPawnDefs   .Where(p => Helpers.GetPawnBioType   (p) == "animal");

                stopwatch.Start();
                DefInjector.InjectSurgeryRecipes(surgeryList, pawnList);
                stopwatch.Stop();

                Logger.Message(afterMsg, "animal", "other animals", stopwatch.ElapsedMilliseconds / 1000f, surgeryList.Count() * pawnList.Count());
            }
            stopwatch.Reset();

            // Humanlike/Humanlike
            if ( ((SettingHandle<bool>)config["PatchHumanlikeToHumanlike"]).Value ) {
                if (IsDebug) Logger.Message(beforeMsg, "humanlike", "other humanlikes");

                var surgeryList = allSurgeryDefs.Where(s => Helpers.GetSurgeryBioType(s) == "humanlike");
                var    pawnList = allPawnDefs   .Where(p => Helpers.GetPawnBioType   (p) == "humanlike");

                stopwatch.Start();
                DefInjector.InjectSurgeryRecipes(surgeryList, pawnList);
                stopwatch.Stop();

                Logger.Message(afterMsg, "humanlike", "other humanlikes", stopwatch.ElapsedMilliseconds / 1000f, surgeryList.Count() * pawnList.Count());
            }
            stopwatch.Reset();

            // */Mech (artificial+mech only)
            if ( ((SettingHandle<bool>)config["PatchArtificialToMech"]).Value ) {
                if (IsDebug) Logger.Message(beforeMsg, "artificial part", "mechs");

                var surgeryList = allSurgeryDefs.Where(s => 
                    Helpers.IsSupertypeOf(typeof(Recipe_InstallArtificialBodyPart), s.workerClass) ||
                    Helpers.IsSupertypeOf("OrenoMSE.Recipe_InstallBodyPartModule",  s.workerClass) ||
                    Helpers.GetSurgeryBioType(s) == "mech"
                );
                var    pawnList = allPawnDefs   .Where(p => Helpers.GetPawnBioType   (p) == "mech");

                stopwatch.Start();
                DefInjector.InjectSurgeryRecipes(surgeryList, pawnList);
                stopwatch.Stop();

                Logger.Message(afterMsg, "artificial part", "mechs", stopwatch.ElapsedMilliseconds / 1000f, surgeryList.Count() * pawnList.Count());
            }
            stopwatch.Reset();

            // Animal/Humanlike
            if ( ((SettingHandle<bool>)config["PatchAnimalToHumanlike"]).Value ) {
                if (IsDebug) Logger.Message(beforeMsg, "animal", "humanlikes");

                var surgeryList = allSurgeryDefs.Where(s => Helpers.GetSurgeryBioType(s) == "animal");
                var    pawnList = allPawnDefs   .Where(p => Helpers.GetPawnBioType   (p) == "humanlike");

                stopwatch.Start();
                DefInjector.InjectSurgeryRecipes(surgeryList, pawnList);
                stopwatch.Stop();

                Logger.Message(afterMsg, "animal", "humanlikes", stopwatch.ElapsedMilliseconds / 1000f, surgeryList.Count() * pawnList.Count());
            }
            stopwatch.Reset();

            // Humanlike/Animal
            if ( ((SettingHandle<bool>)config["PatchHumanlikeToAnimal"]).Value ) {
                if (IsDebug) Logger.Message(beforeMsg, "humanlike", "animals");

                var surgeryList = allSurgeryDefs.Where(s => Helpers.GetSurgeryBioType(s) == "humanlike");
                var    pawnList = allPawnDefs   .Where(p => Helpers.GetPawnBioType   (p) == "animal");

                stopwatch.Start();
                DefInjector.InjectSurgeryRecipes(surgeryList, pawnList);
                stopwatch.Stop();

                Logger.Message(afterMsg, "humanlike", "animals", stopwatch.ElapsedMilliseconds / 1000f, surgeryList.Count() * pawnList.Count());
            }
            stopwatch.Reset();

            // Any Fleshlike to any Fleshlike (only if all other similar ones are on)
            if (
                ((SettingHandle<bool>)config["PatchAnimalToAnimal"])      .Value &&
                ((SettingHandle<bool>)config["PatchHumanlikeToHumanlike"]).Value &&
                ((SettingHandle<bool>)config["PatchAnimalToHumanlike"])   .Value &&
                ((SettingHandle<bool>)config["PatchHumanlikeToAnimal"])   .Value
            ) {
                if (IsDebug) Logger.Message(beforeMsg, "fleshlike", "fleshlikes");

                var surgeryList = allSurgeryDefs.Where(s => Regex.IsMatch( Helpers.GetSurgeryBioType(s), "animal|(?:human|flesh)like|mixed" ));
                var    pawnList = allPawnDefs   .Where(p => Helpers.GetPawnBioType(p) != "mech");

                stopwatch.Start();
                DefInjector.InjectSurgeryRecipes(surgeryList, pawnList);
                stopwatch.Stop();

                Logger.Message(afterMsg, "fleshlike", "fleshlikes", stopwatch.ElapsedMilliseconds / 1000f, surgeryList.Count() * pawnList.Count());
            }
            stopwatch.Reset();

            // Clean up
            if (IsDebug) Logger.Message("Merging duplicate surgical recipes and sorting");

            stopwatch.Start();
            DefInjector.CleanupSurgeryRecipes(allSurgeryDefs, allPawnDefs);
            stopwatch.Stop();

            Logger.Message("Merged duplicate surgical recipes and sorting (took {0:F4}s)", stopwatch.ElapsedMilliseconds / 1000f);
            stopwatch.Reset();

            // No need to occupy all of this memory
            Helpers.ClearCaches();
        }

        public void ProcessSettings () {
            // Hidden config version entry
            Version currentVer    = Instance.GetVersion();
            string  currentVerStr = currentVer.ToString();

            config["ConfigVersion"] = Settings.GetHandle<string>("ConfigVersion", "", "", currentVerStr);
            var configVerSetting = (SettingHandle<string>)config["ConfigVersion"];
            configVerSetting.DisplayOrder = 0;
            configVerSetting.NeverVisible = true;

            string  configVerStr = configVerSetting.Value;
            Version configVer    = new Version(configVerStr);
            
            var settingNames = new List<string> {
                "RestartNoteHeader",

                "BlankHeader",
                "SearchHeader",
                "SearchAdminsterRecipes",
                "SearchInstallNaturalBodyPartRecipes",
                "SearchInstallArtificialBodyPartRecipes",
                "SearchInstallImplantRecipes",
                "SearchVanillaRemovalRecipes",
                "SearchModdedSurgeryClasses",

                "BlankHeader",
                "PatchHeader",
                "PatchAnimalToAnimal",
                "PatchHumanlikeToHumanlike",
                "PatchArtificialToMech",
                "PatchAnimalToHumanlike",
                "PatchHumanlikeToAnimal",
            };
            
            int order = 1;
            foreach (string sName in settingNames) {
                bool isHeader = sName.Contains("Header");

                if (sName == "BlankHeader") {
                    // No translations here
                    config[sName] = Settings.GetHandle<bool>(sName + order, "", "", false);
                }
                else {
                    config[sName] = Settings.GetHandle<bool>(
                        sName,
                        string.Concat(
                            isHeader ? "<size=15><b>" : "",
                            ("XP_" + sName + "_Title").Translate(),
                            isHeader ? "</b></size>" : ""
                        ),
                        ("XP_" + sName + "_Description").Translate(),
                        !isHeader
                    );
                }

                var setting = (SettingHandle<bool>)config[sName];
                setting.DisplayOrder = order;

                if (isHeader) {
                    // No real settings here; just for display
                    setting.Unsaved = true;
                    setting.CustomDrawer = rect => { return false; };
                }

                order++;
            }

            // Set the new config value to the current version
            configVer                             = currentVer;
            configVerStr = configVerSetting.Value = currentVerStr;
        }

    }
}
