﻿using System;
using Illumina.Common;

namespace Isas.Shared
{

    // ReSharper disable InconsistentNaming - prevents ReSharper from renaming serializeable members that are sensitive to being changed
    public class VariantCallingFilterSettings
    {
        public bool FilterOutSingleProbePoolVariants = false; // PISCES (unused!)
        public bool FilterOutSingleStrandVariants = false; // PISCES, GATK
        public int IndelRepeatFilterCutoff = -1; // All.  Used in MiSeq and with starling.
        public int MinQScore = -1; // Starling; a basecall-level (not variant-level) filter
        public int MinimumCoverageDepthEmitCutoff = -1; // PISCES
        public int MinimumDepthCutoff = -1; // All.
        public int MinimumGQCutoff = -1; // All.  GQ tag.  
        public float MinimumGQXCutoff = -1f; // All.  GQX = min(Qual, GQ).  Used on HiSeq (MiSeq still uses GQ except for starling which uses GQX)

        public float MinimumMQCutoff = -1f; // GATK
        public float MinimumQDCutoff = -1f; // GATK
        public int MinimumQualCutoff = -1; // All.  Not used at all by default.

        public int MinimumQualityEmitCutoff = -1; // PISCES
        public int MaximumQualityScore = 100; // PISCES
        public bool OnlyCallInROI = false; // PISCES
        public bool ReportPercentNoCalls = false; // PISCES   
        public bool OnlyUseProperPairs = false; // PISCES
        public bool CallPhasedSNVs = true; // PISCES  turning this on by default! 8/19/14
        public float StrandBiasFilterCutoff = 0f; // PISCES, GATK, starling (note: starling uses this for SNVs only)
        public int MaxSizePhasedSNV = 3; // PISCES lowering this to 3 for speed.
        public int MaxGapPhasedSNV = 1; // PISCES lowering this to 1 for speed
        //public string QualityModel = "Poisson"; // PISCES depracated. *this* has to be "Poisson"
        public double VariantFrequencyEmitCutoff = -1; // PISCES
        public double VariantFrequencyFilterCutoff = -1; // All.  Used in MiSeq only.
        public bool FilterOffTargetVariants = false; //Used by multiple workflows, + pisces, gatk, and starling. 

        public bool FilterDPFReads = true; //starling
        public bool DisableStrandBiasFilter = false;//starling
        public bool DisableHPOLFilter = false;//starling
        public bool DisableIndelMaxCoverageFilter = false;//starling
        // ReSharper restore InconsistentNaming

        public VariantCallingFilterSettings Clone()
        {
            return (VariantCallingFilterSettings)MemberwiseClone();
        }

        /// <summary>
        ///     Master method for setting the default variant filters for our workflow and variant caller
        ///     (plus a couple of settings inherited from the config file)
        /// </summary>
        public void SetDefaultVariantFilters(SecondaryAnalysisWorkflow secondaryAnalysisWorkflow,
            SupportedVariantCallers variantCaller, int configMinimumGQ, int configIndelRepeat)
        {
            // Pisces applies the single-strand filter by default...except for TSCA, where some bases truly should be covered on just one strand.
            if (secondaryAnalysisWorkflow.WorkflowSettings.VariantCaller == SupportedVariantCallers.Somatic)
            {
                FilterOutSingleStrandVariants = true;
            }

            // starling uses a default filter for StrandBias
            if (variantCaller == SupportedVariantCallers.Starling)
            {
                StrandBiasFilterCutoff = 10;
            }

            // Default for Pisces:
            if ((MinimumQualityEmitCutoff < 0) && (variantCaller == SupportedVariantCallers.Somatic))
            {
                MinimumQualityEmitCutoff = 20; //only Pisces uses this 
            }

            // Default GQ: if the user specified something in the Isas Config, use that.
            if (configMinimumGQ > 0)
            {
                MinimumGQCutoff = configMinimumGQ;
            }
            else //if not, pick a reasonable default.
            {
                switch (variantCaller)
                {
                    case SupportedVariantCallers.None:
                    case SupportedVariantCallers.Starling:
                        MinimumGQCutoff = 0;
                        break;
                    case SupportedVariantCallers.Somatic:
                        MinimumGQCutoff = 30;
                        break;
                    case SupportedVariantCallers.GATK:
                        MinimumGQCutoff = 0;
                        break;
                    default:
                        throw new Exception(string.Format("Error: Unknown variant caller '{0}'", variantCaller));
                }
            }

            if (configIndelRepeat < 0) // -1 means "set a default for me":
            {
                if (variantCaller == SupportedVariantCallers.Somatic)
                {
                    IndelRepeatFilterCutoff = 8;
                }
                else
                {
                    IndelRepeatFilterCutoff = 0;
                }
            }
            else
            {
                IndelRepeatFilterCutoff = configIndelRepeat;
            }

            // VariantFrequency:
            switch (variantCaller)
            {
                case SupportedVariantCallers.None:
                case SupportedVariantCallers.Starling:
                case SupportedVariantCallers.GATK:
                    VariantFrequencyFilterCutoff = 0;
                    break;
                case SupportedVariantCallers.Somatic:
                    VariantFrequencyFilterCutoff = 0.01;
                    break;
                default:
                    throw new Exception(string.Format("Error: Unknown variant caller '{0}'", variantCaller));
            }

            //VariantFrequencyEmitCutoff
            if (VariantFrequencyEmitCutoff < 0)
            {
                switch (variantCaller)
                {
                    case SupportedVariantCallers.None:
                    case SupportedVariantCallers.Starling:
                        VariantFrequencyEmitCutoff = 0.1; // %%% Unused by Starling?
                        break;
                    case SupportedVariantCallers.GATK:
                        VariantFrequencyEmitCutoff = 0.00;
                        break;
                    case SupportedVariantCallers.Somatic:
                        VariantFrequencyEmitCutoff = 0.01;
                        break;
                    default:
                        throw new Exception(string.Format("Error: Unknown variant caller '{0}'", variantCaller));
                }
            }

            if (MinimumCoverageDepthEmitCutoff < 0 && variantCaller == SupportedVariantCallers.Somatic)
            {
                MinimumCoverageDepthEmitCutoff = 10;
            }

            if (MinimumQDCutoff < 0)
            {
                switch (variantCaller)
                {
                    case SupportedVariantCallers.GATK:
                        MinimumQDCutoff = 2;
                        break;
                    default:
                        MinimumQDCutoff = 0;
                        break;
                }
            }

            if (MinimumGQXCutoff < 0)
            {
                switch (variantCaller)
                {
                    case SupportedVariantCallers.GATK:
                        MinimumGQXCutoff = 30;
                        break;
                    case SupportedVariantCallers.Starling:
                        MinimumGQXCutoff = 30;
                        break;
                    default:
                        MinimumGQXCutoff = 0;
                        break;
                }
            }

            if (MinimumMQCutoff < 0 && variantCaller == SupportedVariantCallers.GATK)
            {
                MinimumMQCutoff = 20;
            }

            // Default strand bias for Pisces:
            if (variantCaller == SupportedVariantCallers.Somatic)
            {
                StrandBiasFilterCutoff = 0.5f;
            }

            OnlyCallInROI = false;

            // MinQScore:
            switch (variantCaller)
            {
                case SupportedVariantCallers.Starling:
                    MinQScore = 17;
                    break;
                case SupportedVariantCallers.Somatic:
                    MinQScore = 20;
                    break;
                default:
                    MinQScore = 0; // not used by gatk, anyway!
                    break;
            }
        }
    }
}
