﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CanvasCommon;
using NDesk.Options;
using System.IO;
using SequencingFiles;

namespace CanvasClean
{
    class CanvasClean
    {
        private static readonly int numberOfGCbins = 101;
        private static readonly int defaultMinNumberOfBinsPerGC = 100;
        private static int minNumberOfBinsPerGCForWeightedMedian = 100;

        /// <summary>
        /// Displays help at the command line.
        /// </summary>
        /// <param name="p">NDesk OptionSet containing command line parameters.</param>
        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: CanvasClean.exe [OPTIONS]+");
            Console.WriteLine("Correct bin counts based on genomic parameters");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }

        /// <summary>
        /// Debugging option - save off a table of counts by GC bin.  
        /// </summary>
        static void DebugPrintCountsByGC(List<GenomicBin> bins, string filePath)
        {
            int[][] HistogramByGC = new int[numberOfGCbins][];
            for (int GC = 0; GC < HistogramByGC.Length; GC++) HistogramByGC[GC] = new int[1024];
            foreach (GenomicBin bin in bins)
            {
                if (bin.Count < 0 || bin.Count >= 1024) continue;
                HistogramByGC[bin.GC][(int)bin.Count]++;
            }
            using (StreamWriter writer = new StreamWriter(filePath))
            {
                // Header line:
                writer.Write("#Bin\\GC");
                for (int GC = 0; GC < HistogramByGC.Length; GC++)
                    writer.Write("GC{0}\t", GC);
                writer.WriteLine();
                // Body:
                for (int count = 0; count < 1024; count++)
                {
                    writer.Write("{0}\t", count);
                    for (int GC = 0; GC < HistogramByGC.Length; GC++)
                        writer.Write("{0}\t", HistogramByGC[GC][count]);
                    writer.WriteLine();
                }
            }
            Console.WriteLine("Wrote counts-by-GC histogram to {0}", filePath);
        }

        public static IEnumerable<GenomicBin> GetOnTargetBins(IEnumerable<GenomicBin> bins, NexteraManifest manifest) 
        {
            var regionsByChrom = manifest.GetManifestRegionsByChromosome();
            string currChrom = null;
            List<NexteraManifest.ManifestRegion> regions = null; // 1-based regions
            int regionIndex = -1;
            bool offTarget = true;
            foreach (GenomicBin bin in bins) // 0-based bins
            {
                if (currChrom != bin.Chromosome)
                {
                    currChrom = bin.Chromosome;
                    offTarget = true;
                    if (!regionsByChrom.ContainsKey(currChrom))
                    {
                        regions = null;
                    }
                    else
                    {
                        regions = regionsByChrom[currChrom];
                        regionIndex = 0;
                    }
                }
                while (regions != null && regionIndex < regions.Count && regions[regionIndex].End < bin.Start + 1)
                {
                    regionIndex++;
                }
                if (regions != null && regionIndex < regions.Count && regions[regionIndex].Start <= bin.Stop) // overlap
                {
                    offTarget = false;
                }
                else
                {
                    offTarget = true;
                }

                if (offTarget) { continue; } // ignore off-target bins

                yield return bin;
            }
        }

        /// <summary>
        /// Assumes the bins are sorted by genomic coordinates
        /// </summary>
        /// <param name="bins">Bins whose counts are to be normalized</param>
        /// <param name="countsByGC">An array of lists. Each array element (0-100) will hold a list of counts whose bins have the same GC content.</param>
        /// <param name="counts">Will hold all of the autosomal counts present in 'bins'</param>
        static void GetCountsByGC(List<GenomicBin> bins, NexteraManifest manifest, out List<float>[] countsByGC, out List<float> counts)
        {
            countsByGC = new List<float>[numberOfGCbins];
            counts = new List<float>(bins.Count);

            // Initialize the lists
            for (int i = 0; i < countsByGC.Length; i++)
                countsByGC[i] = new List<float>();

            foreach (GenomicBin bin in manifest == null ? bins : GetOnTargetBins(bins, manifest))
            {
                if (!GenomeMetadata.SequenceMetadata.IsAutosome(bin.Chromosome)) { continue; }

                // Put the observed count in the GC-appropriate list.
                countsByGC[bin.GC].Add(bin.Count);

                // Add to the global list of counts.
                counts.Add(bin.Count);
            }
        }

        /// <summary>
        /// Perform variance stabilization by GC bins.
        /// </summary>
        /// <param name="bins">Bins whose counts are to be normalized.</param>
        static bool NormalizeVarianceByGC(List<GenomicBin> bins, NexteraManifest manifest = null)
        {
            // DebugPrintCountsByGC(bins, "CountsByGCVariance-Before.txt");
            // An array of lists. Each array element (0-100) will hold a list of counts whose bins have the same GC content.
            List<float>[] countsByGC;
            // Will hold all of the autosomal counts present in 'bins'
            List<float> counts;
            GetCountsByGC(bins, manifest, out countsByGC, out counts);

            // Estimate quartiles of all bins genomewide
            var globalQuartiles = CanvasCommon.Utilities.Quartiles(counts);
            // Will hold interquartile range (IQR) separately for each GC bin
            List<float> localIQR = new List<float> (countsByGC.Length);
            // Will hold quartiles separately for each GC bin
            List<Tuple<float, float, float>> localQuartiles = new List<Tuple<float, float, float>>(countsByGC.Length);

            // calculate interquartile range (IQR) for GC bins and populate localQuartiles list
            for (int i = 0; i < countsByGC.Length; i++)
            {
                if (countsByGC[i].Count == 0) 
                {
                    localIQR.Add(-1f);     
                    localQuartiles.Add(new Tuple<float, float, float>(-1f, -1f, -1f));
                }
                else if (countsByGC[i].Count >= defaultMinNumberOfBinsPerGC)
                {
                    localQuartiles.Add(CanvasCommon.Utilities.Quartiles(countsByGC[i]));
                    localIQR.Add(localQuartiles[i].Item3 - localQuartiles[i].Item1);
                }
                else
                {
                    List<Tuple<float, float>> weightedCounts = GetWeightedCounts(countsByGC, i);
                    double[] quartiles = CanvasCommon.Utilities.WeightedQuantiles(weightedCounts, new List<float>() { 0.25f, 0.75f });
                    localIQR.Add((float)(quartiles[1] - quartiles[0]));
                }
            }

            // Identify if particular GC bins have IQR twice as large as IQR genomewide 
            float globalIQR = globalQuartiles.Item3 - globalQuartiles.Item1;
            // Holder for GC bins with large IQR (compared to genomewide IQR)
            int significantIQRcounter = 0;
            for (int i = 10; i < 90; i++)
            {
                if (globalIQR < localIQR[i] * 2f)
                    significantIQRcounter ++;
            }

            if (significantIQRcounter > 0) 
            {
                // Divide each count by the median count of bins with the same GC content
                for (int i = 0; i < bins.Count; i++)
                {
                    if (globalIQR < localIQR[bins[i].GC] * 0.8f)
                    {
                        // ratio of GC bins and global IQRs
                        float ratioIQR = localIQR[bins[i].GC] / globalIQR;
                        if (bins[i].Count > localQuartiles[bins[i].GC].Item2)
                            bins[i].Count = localQuartiles[bins[i].GC].Item2 + (bins[i].Count - localQuartiles[bins[i].GC].Item2) / (ratioIQR * 0.8f);
                        else
                            bins[i].Count = localQuartiles[bins[i].GC].Item2 - (bins[i].Count - localQuartiles[bins[i].GC].Item2) / (ratioIQR * 0.8f);
                    }
                }            
            }
            if (significantIQRcounter > 0)
                return true;
            else 
                return false;
            //DebugPrintCountsByGC(bins, "CountsByGCVariance-After.txt");
        }

        /// <summary>
        /// In case there are not enough read counts for a GC bin, construct a weighted list of read counts.
        /// Values from target bin i get full weight, the two neighboring bins get half weight, two-away
        /// neighbors get 1/4 weight, etc.
        /// </summary>
        /// <param name="countsByGC"></param>
        /// <param name="gcBin"></param>
        /// <returns></returns>
        private static List<Tuple<float, float>> GetWeightedCounts(List<float>[] countsByGC, int gcBin)
        {
            List<Tuple<float, float>> weightedCounts = new List<Tuple<float, float>>();
            int radius = 0;
            float weight = 1;
            while (weightedCounts.Count < defaultMinNumberOfBinsPerGC) 
            {
                int gcWindowEnd = gcBin + radius;
                int gcWindowStart = gcBin - radius;
                if (gcWindowEnd >= countsByGC.Length && gcWindowStart < 0) { break; }

                if (gcWindowEnd < countsByGC.Length) 
                {
                    weightedCounts.AddRange(countsByGC[gcWindowEnd].Select(c => Tuple.Create(c, weight)));
                }

                if (gcWindowStart != gcWindowEnd && gcWindowStart >= 0) 
                {
                    weightedCounts.AddRange(countsByGC[gcWindowStart].Select(c => Tuple.Create(c, weight)));
                }
                radius++;
                weight /= 2;
            }

            return weightedCounts;
        }

        /// <summary>
        /// Perform a simple GC normalization.
        /// </summary>
        /// <param name="bins">Bins whose counts are to be normalized.</param>
        /// <param name="skipZeros">Skip bins with zero count</param>
        static void NormalizeByGC(List<GenomicBin> bins, NexteraManifest manifest = null)
        {
            //DebugPrintCountsByGC(bins, "CountsByGC-Before.txt");
            // An array of lists. Each array element (0-100) will hold a list of counts whose bins have the same GC content.
            List<float>[] countsByGC;

            // Will hold all of the autosomal counts present in 'bins'
            List<float> counts;
            GetCountsByGC(bins, manifest, out countsByGC, out counts);

            double globalMedian = CanvasCommon.Utilities.Median(counts);
            double?[] medians = new double?[countsByGC.Length];

            // Compute the median count for each GC bin
            for (int gcBinIndex = 0; gcBinIndex < countsByGC.Length; gcBinIndex++)
            {
                if (countsByGC[gcBinIndex].Count >= defaultMinNumberOfBinsPerGC)
                {
                    medians[gcBinIndex] = CanvasCommon.Utilities.Median(countsByGC[gcBinIndex]);
                }
                else
                {
                    List<Tuple<float, float>> weightedCounts = GetWeightedCounts(countsByGC, gcBinIndex);              
                    medians[gcBinIndex] = CanvasCommon.Utilities.WeightedMedian(weightedCounts);
                }
            }

            // Divide each count by the median count of bins with the same GC content
            for (int gcBinIndex = 0; gcBinIndex < bins.Count; gcBinIndex++)
            {
                double? median = medians[bins[gcBinIndex].GC];
                if (median != null && median > 0)
                    bins[gcBinIndex].Count = (float)(globalMedian * (double)bins[gcBinIndex].Count / median);
            }
            //DebugPrintCountsByGC(bins, "CountsByGC-After.txt");
        }

        /// <summary>
        /// Remove bins with extreme GC content.
        /// </summary>
        /// <param name="bins">Genomic bins in from which we filter out GC content outliers.</param>
        /// <param name="threshold">Minimum number of bins with the same GC content required to keep a bin.</param>
        /// 
        /// The rationale of this function is that a GC normalization is performed by computing the median count
        /// for each possible GC value. If that count is small, then the corresponding normalization constant
        /// is unstable and we shouldn't use these data.
        static List<GenomicBin> RemoveBinsWithExtremeGC(List<GenomicBin> bins, int threshold, NexteraManifest manifest = null)
        {
            // Will hold outlier-removed bins.
            List<GenomicBin> stripped = new List<GenomicBin>();

            // used to count the number of bins with each possible GC content (0-100)
            int[] counts = new int[numberOfGCbins];
            double totalCount = 0;
            foreach (GenomicBin bin in manifest == null ? bins : GetOnTargetBins(bins, manifest))
            {

                // We only count autosomal bins because these are the ones we computed normalization factor upon.
                if (!GenomeMetadata.SequenceMetadata.IsAutosome(bin.Chromosome))
                    continue;

                counts[bin.GC]++;
                totalCount++;
            }

            int averageCountPerGC = Math.Max(minNumberOfBinsPerGCForWeightedMedian, (int)(totalCount / counts.Length));
            threshold = Math.Min(threshold, averageCountPerGC);
            foreach (GenomicBin bin in bins)
            {
                // Remove outlier (not a lot of bins with the same GC content)
                if (counts[bin.GC] < threshold)
                    continue;
                stripped.Add(bin);
            }

            return stripped;
        }


        /// <summary>
        /// Calculates Standard Deviation separately for each chromosome and output their average 
        /// </summary>
        static public double LocalStandardDeviation(List<double> list, List<string> chromosome)
        {

            List<double> StandardDeviations = new List<double>();
            List<double> temp = new List<double>();

            for (int iterator = 0; iterator < list.Count - 2; iterator++)
            {
                if (chromosome[iterator] == chromosome[iterator + 1])
                {
                    temp.Add(list[iterator]);
                }
                else
                {
                    StandardDeviations.Add(CanvasCommon.Utilities.Mad(temp, 1, temp.Count));
                    temp.Clear();
                }
            }
            return StandardDeviations.Average();
        }

        /// <summary>
        /// Estimate local standard deviation (SD).
        /// </summary>
        /// <param name="bins">Genomic bins from which we filter out local SD outliers associated with FFPE biases.</param>
        /// <param name="threshold">Median SD value which is used to determine whereas to run RemoveBinsWithExtremeLocalMad on a sample and which set of bins to remove (set as threshold*5).</param>
        /// The rationale of this function is that standard deviation of difference of consecutive bins values, when taken over a small range of bin (i.e. 20 bins),
        /// has a distinct distribution for FFPE compared to Fresh Frozen (FF) samples. This property is used to flag and remove such bins.

        static double getLocalStandardDeviation(List<GenomicBin> bins)
        {
            // Will hold FFPE outlier-removed bins 
            List<GenomicBin> strippedBins = new List<GenomicBin>();

            // Will hold consecutive bin count difference (approximates Skellam Distribution: mean centred on zero so agnostic to CN changes)
            double[] countsDiffs = new double[bins.Count - 1];

            for (int binIndex = 0; binIndex < bins.Count - 1; binIndex++)
            {
                countsDiffs[binIndex] = System.Convert.ToDouble(bins[binIndex + 1].Count - bins[binIndex].Count);
            }

            // holder of local SD values (SDs of 20 bins)
            List<double> localSDs = new List<double>();
            List<string> chromosomeBin = new List<string>();

            // calculate local SD metric
            int windowSize = 20;
            for (int windowEnd = windowSize, windowStart = 0; windowEnd < countsDiffs.Length; windowStart += windowSize, windowEnd += windowSize)
            {
                double localSD = CanvasCommon.Utilities.StandardDeviation(countsDiffs, windowStart, windowEnd);
                localSDs.Add(localSD);
                chromosomeBin.Add(bins[windowStart].Chromosome);
                for (int binIndex = windowStart; binIndex < windowEnd; binIndex += 1)
                {
                    bins[binIndex].MadOfDiffs = localSD;
                }
            }

            // average of local SD metric
            double localSDaverage = LocalStandardDeviation(localSDs, chromosomeBin);
            return localSDaverage;
        }

        /// <summary>
        /// Remove bin regions with extreme local standard deviation (SD).
        /// </summary>
        /// <param name="bins">Genomic bins from which we filter out local SD outliers associated with FFPE biases.</param>
        /// <param name="threshold">Median SD value which is used to determine whereas to run RemoveBinsWithExtremeLocalMad on a sample and which set of bins to remove (set as threshold*5).</param>
        /// The rationale of this function is that standard deviation of difference of consecutive bins values, when taken over a small range of bin (i.e. 20 bins),
        /// has a distinct distribution for FFPE compared to Fresh Frozen (FF) samples. This property is used to flag and remove such bins.

        static List<GenomicBin> RemoveBinsWithExtremeLocalSD(List<GenomicBin> bins, double localSDaverage, double threshold, string outFile)
        {
            // Will hold FFPE outlier-removed bins 
            List<GenomicBin> strippedBins = new List<GenomicBin>();

            // Will hold consecutive bin count difference (approximates Skellam Distribution: mean centred on zero so agnostic to CN changes)
            double[] countsDiffs = new double[bins.Count - 1];

            for (int binIndex = 0; binIndex < bins.Count - 1; binIndex++)
            {
                countsDiffs[binIndex] = System.Convert.ToDouble(bins[binIndex + 1].Count - bins[binIndex].Count);
            }

            // holder of local SD values (SDs of 20 bins)
            List<double> localSDs = new List<double>();
            List<string> chromosomeBin = new List<string>();

            // calculate local SD metric
            int windowSize = 20;
            for (int windowEnd = windowSize, windowStart = 0; windowEnd < countsDiffs.Length; windowStart += windowSize, windowEnd += windowSize)
            {
                double localSD = CanvasCommon.Utilities.StandardDeviation(countsDiffs, windowStart, windowEnd);
                localSDs.Add(localSD);
                chromosomeBin.Add(bins[windowStart].Chromosome);
                for (int binIndex = windowStart; binIndex < windowEnd; binIndex += 1)
                {
                    bins[binIndex].MadOfDiffs = localSD;
                }
            }

            // remove bins with extreme local SD (populating new list is faster than removing from existing one)
            foreach (GenomicBin bin in bins)
            {
                // do not strip bins for samples with local SD metric average less then the threshold
                if (bin.MadOfDiffs > threshold * 2.0 && localSDaverage > 5.0)
                    continue;
                strippedBins.Add(bin);
            }
            return strippedBins;    
        }

        /// <summary>
        /// Removes bins that are genomically large. Typically centromeres and other nasty regions.
        /// </summary>
        /// <param name="bins">Genomic bins.</param>
        static List<GenomicBin> RemoveBigBins(List<GenomicBin> bins)
        {
            List<int> sizes = new List<int>(bins.Count);

            foreach (GenomicBin bin in bins)
                sizes.Add(bin.Size);

            sizes.Sort();

            // Get the 98th percentile of bin sizes
            int index = (int)(0.98 * (double)bins.Count);
            if (index >= sizes.Count)
            {
                Console.Error.WriteLine("Warning in CanvasClean: Too few bins to do outlier removal");
                return bins;
            }
            int thresh = sizes[index];

            List<GenomicBin> stripped = new List<GenomicBin>();

            // Remove bins whose size is greater than the 98th percentile
            foreach (GenomicBin bin in bins)
            {
                if (bin.Size <= thresh)
                    stripped.Add(bin);
            }
            return stripped;
        }

        /// <summary>
        /// Determine if two Poisson counts are unlikely to have come from the same distribution.
        /// </summary>
        /// <param name="a">First count to compare.</param>
        /// <param name="b">Second count to compare.</param>
        /// <returns>True if a and b are unlikely to have arisen from the same Poisson distribution.</returns>
        static bool SignificantlyDifferent(float a, float b)
        {

            double mu = ((double)a + (double)b) / 2;

            if (a + b == 0)
                return false;

            // Calculate Chi-Squared statistic
            double da = (double)a - mu;
            double db = (double)b - mu;
            double chi2 = (da * da + db * db) / mu;

            // Is Chi-Squared greater than the 99th percentile of the Chi-Squared distribution with 1 degree of fredom?
            if (chi2 > 6.635)
                return true;

            return false;
        }

        /// <summary>
        /// Removes point outliers from the dataset.
        /// </summary>
        /// <param name="bins">Genomic bins.</param>
        static List<GenomicBin> RemoveOutliers(List<GenomicBin> bins)
        {
            List<GenomicBin> stripped = new List<GenomicBin>();

            // Check each point to see if it is different than both the point to left and the point to the right
            for (int binIndex = 0; binIndex < bins.Count; binIndex++)
            {
                bool hasPreviousBin = binIndex > 0;
                bool hasNextBin = binIndex < bins.Count - 1;
                string currentBinChromosome = bins[binIndex].Chromosome;
                string previousBinChromosome =  hasPreviousBin ? bins[binIndex - 1].Chromosome : null;
                string nextBinChromosome = hasNextBin ? bins[binIndex + 1].Chromosome : null;
                // Different chromosome on both sides
                if ((hasPreviousBin && !currentBinChromosome.Equals(previousBinChromosome))
                    && (hasNextBin && !currentBinChromosome.Equals(nextBinChromosome)))
                    continue;
                // Same chromosome on at least on side or it's the only bin
                if ((hasPreviousBin && bins[binIndex].Chromosome.Equals(previousBinChromosome) && !SignificantlyDifferent(bins[binIndex].Count, bins[binIndex - 1].Count))
                    || (hasNextBin && bins[binIndex].Chromosome.Equals(nextBinChromosome) && !SignificantlyDifferent(bins[binIndex].Count, bins[binIndex + 1].Count))
                    || (!hasPreviousBin && !hasNextBin))
                {
                    stripped.Add(bins[binIndex]);
                }
            }

            return stripped;
        }

        static int Main(string[] args)
        {
            CanvasCommon.Utilities.LogCommandLine(args);
            string inFile = null;
            string outFile = null;
            bool doGCnorm = false;
            bool doSizeFilter = false;
            bool doOutlierRemoval = false;
            string ffpeOutliersFile = null;
            string manifestFile = null;
            bool needHelp = false;

            OptionSet p = new OptionSet()
            {
                { "i|infile=",        "input file - usually generated by CanvasBin",      v => inFile = v },
                { "o|outfile=",       "text file to output containing cleaned bins",      v => outFile = v },
                { "g|gcnorm",         "perform GC normalization",                         v => doGCnorm = v != null },
                { "s|filtsize",       "filter out genomically large bins",                v => doSizeFilter = v != null },
                { "r|outliers",       "filter outlier points",                            v => doOutlierRemoval = v != null },
                { "f|ffpeoutliers=",   "filter regions of FFPE biases",                   v => ffpeOutliersFile = v },
                { "t|manifest=",      "Nextera manifest file",                            v => manifestFile = v },
                { "w|weightedmedian=", "Minimum number of bins per GC required to calculate weighted median", v => minNumberOfBinsPerGCForWeightedMedian = int.Parse(v) },
                { "h|help",           "show this message and exit",                       v => needHelp = v != null },
            };

            List<string> extraArgs = p.Parse(args);

            if (needHelp)
            {
                ShowHelp(p);
                return 0;
            }

            if (inFile == null || outFile == null)
            {
                ShowHelp(p);
                return 0;
            }

            // Does the input file exist?
            if (!File.Exists(inFile))
            {
                Console.WriteLine("CanvasClean.exe: File {0} does not exist! Exiting.", inFile);
                return 1;
            }

            List<GenomicBin> bins = CanvasIO.ReadFromTextFile(inFile);

            if (doOutlierRemoval)
                bins = RemoveOutliers(bins);

            if (doSizeFilter)
                bins = RemoveBigBins(bins);

            // do not run FFPE outlier removal on targeted/low coverage data
            if (ffpeOutliersFile != null && bins.Count < 50000)
            {
                ffpeOutliersFile = null;
            }

            // estimate localSD metric to use in doFFPEOutlierRemoval later and write to a text file 
            double LocalSD = -1.0;
            if (ffpeOutliersFile != null) 
            {
                LocalSD = getLocalStandardDeviation(bins);
                CanvasIO.WriteLocalSDToTextFile(ffpeOutliersFile, LocalSD);            
            }

            if (doGCnorm)
            {
                NexteraManifest manifest = manifestFile == null ? null : new NexteraManifest(manifestFile, null, Console.WriteLine);
                List<GenomicBin> strippedBins = RemoveBinsWithExtremeGC(bins, defaultMinNumberOfBinsPerGC, manifest: manifest);
                if (strippedBins.Count == 0)
                {
                    Console.Error.WriteLine("Warning in CanvasClean: Coverage too low to perform GC correction; proceeding without GC correction");
                }
                else
                {
                    bins = strippedBins;
                    NormalizeByGC(bins, manifest: manifest);
                    // Use variance normalization only on large exome panels and whole genome sequencing
                    // The treshold is set to 10% of an average number of bins on CanvasClean data
                    if (bins.Count > 500000)
                    {
                        bool isNormalizeVarianceByGC = NormalizeVarianceByGC(bins, manifest: manifest);
                        // If normalization by variance was run (isNormalizeVarianceByGC), perform mean centering by using NormalizeByGC 
                        if (isNormalizeVarianceByGC)
                            NormalizeByGC(bins);
                    }

                }
            }

            if (ffpeOutliersFile != null)
            {
                // threshold 20 is derived to separate FF and noisy FFPE samples (derived from a training set of approx. 40 samples)
                List<GenomicBin> LocalMadstrippedBins = RemoveBinsWithExtremeLocalSD(bins, LocalSD, 20, outFile);
                bins = LocalMadstrippedBins;
            }

            CanvasIO.WriteToTextFile(outFile, bins);
            return 0;

        }

    }

}
