using System;
using System.Runtime.Serialization;

namespace GSwitcher.Models
{
    public enum PowerPlanProfileBoostMode
    {
        Disabled = 0,
        Enabled = 1,
        Aggressive = 2,
        EfficientEnabled = 3,
        EfficientAggressive = 4
    }

    [DataContract]
    public class PowerPlanProfile
    {
        [DataMember]
        public string Id { get; set; }

        [DataMember]
        public string Name { get; set; }
        
        [DataMember]
        public string Description { get; set; }

        [DataMember]
        public int MinProcessorState { get; set; }

        [DataMember]
        public int? MaxPCoreFrequency { get; set; }

        [DataMember]
        public int? MaxECoreFrequency { get; set; }

        [DataMember]
        public PowerPlanProfileBoostMode BoostMode { get; set; }

        [DataMember]
        public int Epp { get; set; }

        [DataMember]
        public bool IsDefault { get; set; }

        public PowerPlanProfile Clone()
        {
            return new PowerPlanProfile
            {
                Id = Id,
                Name = Name,
                Description = Description,
                MinProcessorState = MinProcessorState,
                MaxPCoreFrequency = MaxPCoreFrequency,
                MaxECoreFrequency = MaxECoreFrequency,
                BoostMode = BoostMode,
                Epp = Epp,
                IsDefault = IsDefault
            };
        }

        // --- Factory methods for the "Tested Power Plan" suite ---

        public static PowerPlanProfile CreateTestedEcoProfile()
        {
            return new PowerPlanProfile
            {
                Id = "tested-eco",
                Name = "Tested Eco",
                Description = "Maximum power saving for battery life. CPU boost disabled.",
                MinProcessorState = 5,       // Lower floor is fine for eco
                MaxPCoreFrequency = 2800,    // Low cap for efficiency
                MaxECoreFrequency = 2200,    // Low cap for efficiency
                BoostMode = PowerPlanProfileBoostMode.Disabled, // Key for power saving
                Epp = 100,                   // Strongly prefer power saving
                IsDefault = false
            };
        }

        public static PowerPlanProfile CreateTestedCoolProfile()
        {
            return new PowerPlanProfile
            {
                Id = "tested-cool",
                Name = "Tested Cool",
                Description = "Balanced performance for daily use, prioritizing cool and quiet operation.",
                MinProcessorState = 25,      // User requested
                MaxPCoreFrequency = 4100,    // User requested cap, controlled by EPP/Boost
                MaxECoreFrequency = 3000,    // Lower to reduce background heat
                BoostMode = PowerPlanProfileBoostMode.EfficientEnabled, // Smoother, less spiky performance
                Epp = 50,                    // Balanced for good responsiveness
                IsDefault = true
            };
        }

        public static PowerPlanProfile CreateTestedUltraProfile()
        {
            return new PowerPlanProfile
            {
                Id = "tested-ultra",
                Name = "Tested Ultra (4.1 GHz Cap)",
                Description = "Maximum stable performance with a strict 4.1 GHz P-Core and 3.2 GHz E-Core cap.",
                MinProcessorState = 25,      // User requested
                MaxPCoreFrequency = 4100,    // User requested
                MaxECoreFrequency = 3200,    // User requested
                BoostMode = PowerPlanProfileBoostMode.Aggressive, // To ensure it can hit the cap
                Epp = 0,                     // Prioritize performance; auto-tuner will adjust if needed
                IsDefault = false,
            };
        }
    }
}
