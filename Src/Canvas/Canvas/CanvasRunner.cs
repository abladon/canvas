﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using Isas.Shared;
using SequencingFiles;
using Canvas;
using CanvasCommon;
using Illumina.SecondaryAnalysis.Workflow;

namespace Illumina.SecondaryAnalysis
{
    /// <summary>
    /// Run Canvas tools to generate CNV calls:
    /// </summary>
    public class CanvasRunner
    {
        #region Members

        private readonly string _canvasFolder;
        private readonly CanvasCoverageMode _coverageMode = CanvasCoverageMode.TruncatedDynamicRange;
        private readonly int _countsPerBin;
        private readonly ILogger _logger;
        private readonly IWorkManager _workManager;
        private readonly ICheckpointRunner _checkpointRunner;
        private readonly bool _isSomatic;
        private readonly Dictionary<string, string> _customParameters = new Dictionary<string, string>();
        #endregion

        public CanvasRunner(ILogger logger, IWorkManager workManager, ICheckpointRunner checkpointRunner, bool isSomatic, CanvasCoverageMode coverageMode,
            int countsPerBin, Dictionary<string, string> customParameters = null)
        {
            _logger = logger;
            _workManager = workManager;
            _checkpointRunner = checkpointRunner;
            _isSomatic = isSomatic;
            _canvasFolder = Path.Combine(Utilities.GetAssemblyFolder(typeof(CanvasRunner)));
            _coverageMode = coverageMode;
            _countsPerBin = countsPerBin;
            if (customParameters != null) { _customParameters = customParameters; }
        }

        private string SmallestFile(List<string> paths)
        {
            long minFileSize = long.MaxValue;
            string smallestBamPath = null;
            foreach (string path in paths)
            {
                long fileSize = (new FileInfo(path)).Length;
                if (smallestBamPath == null || minFileSize > fileSize)
                {
                    smallestBamPath = path;
                    minFileSize = fileSize;
                }
            }
            return smallestBamPath;
        }

        private int GetBinSize(CanvasCallset callset, string bamPath, List<string> intermediateDataPaths,
            string canvasReferencePath, string canvasBedPath)
        {
            string canvasBinPath = Path.Combine(_canvasFolder, "CanvasBin.exe");
            string executablePath = canvasBinPath;
            if (Utilities.IsThisMono())
                executablePath = Utilities.GetMonoPath();

            StringBuilder commandLine = new StringBuilder();
            if (Utilities.IsThisMono())
            {
                commandLine.AppendFormat("{0} ", canvasBinPath);
            }
            commandLine.AppendFormat("-b \"{0}\" ", bamPath);
            commandLine.AppendFormat("-p "); // Paired-end input mode (Isaac or BWA output)
            commandLine.AppendFormat("-r \"{0}\" ", canvasReferencePath);

            foreach (string path in intermediateDataPaths)
            {
                commandLine.AppendFormat("-i \"{0}\" ", path);
            }

            commandLine.AppendFormat("-y "); // bin size only

            if (callset.IsEnrichment) // manifest
            {
                if (!File.Exists(callset.TempManifestPath)) { NexteraManifestUtils.WriteNexteraManifests(callset.Manifest, callset.TempManifestPath); }
                commandLine.AppendFormat("-t \"{0}\" ", callset.TempManifestPath);
            }

            string outputStub = Path.Combine(Path.GetDirectoryName(callset.BinSizePath), Path.GetFileNameWithoutExtension(callset.BinSizePath));
            commandLine.AppendFormat("-f \"{0}\" -d {1} -o \"{2}\"", canvasBedPath, _countsPerBin, outputStub);

            UnitOfWork binJob = new UnitOfWork()
            {
                ExecutablePath = executablePath,
                LoggingFolder = _workManager.LoggingFolder.FullName,
                LoggingStub = Path.GetFileNameWithoutExtension(callset.BinSizePath),
                CommandLine = commandLine.ToString()
            };
            if (_customParameters.ContainsKey("CanvasBin"))
            {
                binJob.CommandLine = Utilities.MergeCommandLineOptions(binJob.CommandLine, _customParameters["CanvasBin"], true);
            }
            _workManager.DoWorkSingleThread(binJob);

            int binSize;
            using (StreamReader reader = new StreamReader(callset.BinSizePath))
            {
                binSize = int.Parse(reader.ReadLine());
            }

            return binSize;
        }

        /// <summary>
        /// Invoke CanvasBin.  Return null if this fails and we need to abort CNV calling for this sample.
        /// </summary>
        protected string InvokeCanvasBin(CanvasCallset callset, string canvasReferencePath, string canvasBedPath, string ploidyBedPath)
        {
            StringBuilder commandLine = new StringBuilder();
            string canvasBinPath = Path.Combine(_canvasFolder, "CanvasBin.exe");
            string executablePath = canvasBinPath;
            if (Utilities.IsThisMono())
                executablePath = Utilities.GetMonoPath();

            //use bam as input
            if (callset.Bam == null)
            {
                Console.WriteLine("Input bam file not seen for sample {0}_{1} - no CNV calls", callset.SampleName, callset.Id);
                return null;
            }
            List<string> bamPaths = new List<string>();
            bamPaths.Add(callset.Bam.BamFile.FullName);
            if (!(callset.IsEnrichment && callset.Manifest.CanvasControlAvailable)) // do not add normal BAMs if Canvas Control is available
            {
                bamPaths.AddRange(callset.NormalBamPaths.Select(bam => bam.BamFile.FullName));
            }

            // loop over the reference sequences in that genome
            GenomeMetadata genomeMetadata = callset.GenomeMetadata;
            List<UnitOfWork> binJobs = new List<UnitOfWork>();

            Dictionary<string, List<string>> intermediateDataPathsByBamPath = new Dictionary<string, List<string>>();
            foreach (string bamPath in bamPaths) { intermediateDataPathsByBamPath[bamPath] = new List<string>(); }
            for (int bamIndex = 0; bamIndex < bamPaths.Count; bamIndex++)
            {
                foreach (GenomeMetadata.SequenceMetadata sequenceMetadata in genomeMetadata.Sequences.OrderByDescending(sequence => sequence.Length))
                {
                    // Only invoke CanvasBin for autosomes + allosomes;
                    // don't invoke it for mitochondrial chromosome or extra contigs or decoys
                    if (sequenceMetadata.Type != GenomeMetadata.SequenceType.Allosome && !sequenceMetadata.IsAutosome())
                        continue;

                    string bamPath = bamPaths[bamIndex];
                    commandLine.Clear();
                    if (Utilities.IsThisMono())
                    {
                        commandLine.AppendFormat("{0} ", canvasBinPath);
                    }
                    commandLine.AppendFormat("-b \"{0}\" ", bamPath);
                    if (callset.Bam.IsPairedEnd) commandLine.AppendFormat("-p ");
                    commandLine.AppendFormat("-r \"{0}\" ", canvasReferencePath);
                    commandLine.AppendFormat("-c {0} ", sequenceMetadata.Name);
                    commandLine.AppendFormat("-m {0} ", _coverageMode);
                    if (callset.IsEnrichment) // manifest
                    {
                        if (!File.Exists(callset.TempManifestPath)) { NexteraManifestUtils.WriteNexteraManifests(callset.Manifest, callset.TempManifestPath); }
                        commandLine.AppendFormat("-t \"{0}\" ", callset.TempManifestPath);
                    }

                    string intermediateDataPath = Path.Combine(callset.TempFolder, string.Format("{0}_{1}_{2}.dat",
                        callset.Id, bamIndex, sequenceMetadata.Name));
                    intermediateDataPathsByBamPath[bamPath].Add(intermediateDataPath);
                    commandLine.AppendFormat("-f \"{0}\" -d {1} -o \"{2}\" ", canvasBedPath, _countsPerBin, intermediateDataPath);

                    UnitOfWork binJob = new UnitOfWork()
                    {
                        ExecutablePath = executablePath,
                        LoggingFolder = _workManager.LoggingFolder.FullName,
                        LoggingStub = Path.GetFileName(intermediateDataPath),
                        CommandLine = commandLine.ToString()
                    };
                    if (_customParameters.ContainsKey("CanvasBin"))
                    {
                        binJob.CommandLine = Utilities.MergeCommandLineOptions(binJob.CommandLine, _customParameters["CanvasBin"], true);
                    }
                    binJobs.Add(binJob);
                }
            }
            _workManager.DoWorkParallelThreads(binJobs);

            // get bin size (of the smallest BAM) if normal BAMs are given
            int binSize = -1;
            if (bamPaths.Count > 1)
            {
                string smallestBamPath = SmallestFile(bamPaths);
                binSize = GetBinSize(callset, smallestBamPath, intermediateDataPathsByBamPath[smallestBamPath],
                    canvasReferencePath, canvasBedPath);
            }
            else if (callset.IsEnrichment && callset.Manifest.CanvasControlAvailable)
            {
                binSize = callset.Manifest.CanvasBinSize.Value;
            }

            Dictionary<string, string> bamToBinned = new Dictionary<string, string>();
            List<UnitOfWork> finalBinJobs = new List<UnitOfWork>();
            for (int bamIdx = 0; bamIdx < bamPaths.Count; bamIdx++)
            {
                string bamPath = bamPaths[bamIdx];
                // finish up CanvasBin step by merging intermediate data and finally binning                
                string binnedPath = Path.Combine(callset.TempFolder, string.Format("{0}_{1}.binned", callset.Id, bamIdx));
                bamToBinned[bamPath] = binnedPath;
                commandLine.Clear();
                if (Utilities.IsThisMono())
                {
                    commandLine.AppendFormat("{0} ", canvasBinPath);
                }
                commandLine.AppendFormat("-b \"{0}\" ", bamPath);
                if (callset.Bam.IsPairedEnd) commandLine.AppendFormat("-p ");

                commandLine.AppendFormat("-r \"{0}\" ", canvasReferencePath);
                commandLine.AppendFormat("-f \"{0}\" -d {1} -o \"{2}\" ", canvasBedPath, _countsPerBin, binnedPath);
                if (binSize != -1)
                {
                    commandLine.AppendFormat("-z \"{0}\" ", binSize);
                }

                foreach (string path in intermediateDataPathsByBamPath[bamPath])
                {
                    commandLine.AppendFormat("-i \"{0}\" ", path);
                }

                commandLine.AppendFormat("-m {0} ", _coverageMode);

                UnitOfWork finalBinJob = new UnitOfWork()
                {
                    ExecutablePath = executablePath,
                    LoggingFolder = _workManager.LoggingFolder.FullName,
                    LoggingStub = Path.GetFileName(binnedPath),
                    CommandLine = commandLine.ToString()
                };
                if (_customParameters.ContainsKey("CanvasBin"))
                {
                    finalBinJob.CommandLine = Utilities.MergeCommandLineOptions(finalBinJob.CommandLine, _customParameters["CanvasBin"], true);
                }
                finalBinJobs.Add(finalBinJob);
            }
            _workManager.DoWorkParallel(finalBinJobs, new TaskResourceRequirements(8, 25)); // CanvasBin itself is multi-threaded

            string tumorBinnedPath = bamToBinned[callset.Bam.BamFile.FullName]; // binned tumor sample
            string outputPath = tumorBinnedPath;
            if (callset.NormalBamPaths.Any() || (callset.IsEnrichment && callset.Manifest.CanvasControlAvailable))
            {
                outputPath = InvokeCanvasNormalize(callset, tumorBinnedPath, bamToBinned, ploidyBedPath);
            }

            return outputPath;
        }

        /// <summary>
        /// Invoke CanvasNormalize.
        /// </summary>
        /// <param name="callset"></param>
        /// <returns>path to the bin ratio bed file</returns>
        protected string InvokeCanvasNormalize(CanvasCallset callset, string tumorBinnedPath, Dictionary<string, string> bamToBinned,
            string ploidyBedPath, string mode = "weightedaverage")
        {
            string ratioBinnedPath = Path.Combine(callset.TempFolder, string.Format("{0}.ratio.binned", callset.Id));

            string canvasNormalizePath = Path.Combine(_canvasFolder, "CanvasNormalize.exe");
            string executablePath = canvasNormalizePath;
            if (Utilities.IsThisMono())
                executablePath = Utilities.GetMonoPath();

            StringBuilder commandLine = new StringBuilder();
            if (Utilities.IsThisMono())
            {
                commandLine.AppendFormat("{0} ", canvasNormalizePath);
            }

            commandLine.AppendFormat("-t {0} ", tumorBinnedPath.WrapWithShellQuote()); // tumor bed

            if (callset.IsEnrichment && callset.Manifest.CanvasControlAvailable)
            {
                commandLine.AppendFormat("-n {0} ", callset.Manifest.CanvasControlBinnedPath.WrapWithShellQuote()); // normal bed
            }
            else
            {
                foreach (string normalBinnedPath in callset.NormalBamPaths.Select(path => bamToBinned[path.BamFile.FullName]))
                {
                    commandLine.AppendFormat("-n {0} ", normalBinnedPath.WrapWithShellQuote()); // normal bed
                }
            }

            commandLine.AppendFormat("-w {0} ", callset.NormalBinnedPath.WrapWithShellQuote()); // weighted average normal bed

            commandLine.AppendFormat("-o {0} ", ratioBinnedPath.WrapWithShellQuote()); // ratio bed

            if (callset.IsEnrichment) // manifest
            {
                if (!File.Exists(callset.TempManifestPath)) { NexteraManifestUtils.WriteNexteraManifests(callset.Manifest, callset.TempManifestPath); }
                commandLine.AppendFormat("-f {0} ", callset.TempManifestPath.WrapWithShellQuote());
            }

            commandLine.AppendFormat("-m {0} ", mode.WrapWithShellQuote());

            if (!string.IsNullOrEmpty(ploidyBedPath))
            {
                commandLine.AppendFormat("-p {0} ", ploidyBedPath.WrapWithShellQuote());
            }

            UnitOfWork normalizeJob = new UnitOfWork()
            {
                ExecutablePath = executablePath,
                LoggingFolder = _workManager.LoggingFolder.FullName,
                LoggingStub = Path.GetFileName(ratioBinnedPath),
                CommandLine = commandLine.ToString()
            };
            if (_customParameters.ContainsKey("CanvasNormalize"))
            {
                normalizeJob.CommandLine = Utilities.MergeCommandLineOptions(normalizeJob.CommandLine, _customParameters["CanvasNormalize"], true);
            }
            _workManager.DoWorkSingleThread(normalizeJob);

            return ratioBinnedPath;
        }

        /// <summary>
        /// Intersect bins with the targeted regions defined in callset.Manifest.
        /// Assumes that the targeted regions don't intersect, the bins are sorted by genomic location and the bins don't intersect.
        /// </summary>
        /// <param name="callset"></param>
        /// <param name="partitionedPath">Output of CanvasPartition. Bins are assumed to be sorted</param>
        /// <returns></returns>
        private string IntersectBinsWithTargetedRegions(CanvasCallset callset, string partitionedPath)
        {
            if (!File.Exists(partitionedPath)) { return partitionedPath; }
            string rawPartitionedPath = partitionedPath + ".raw";
            if (File.Exists(rawPartitionedPath)) { File.Delete(rawPartitionedPath); }
            File.Move(partitionedPath, rawPartitionedPath);

            //callset.Manifest
            Dictionary<string, List<NexteraManifest.ManifestRegion>> manifestRegionsByChrom = callset.Manifest.GetManifestRegionsByChromosome();

            // CanvasPartition output file is in the BED format
            //   start: 0-based, inclusive
            //   end: 0-based, exclusive
            // Manifest
            //   start: 1-based, inclusive
            //   end: 1-based, inclusive
            using (GzipReader reader = new GzipReader(rawPartitionedPath))
            using (GzipWriter writer = new GzipWriter(partitionedPath))
            {
                string currentChrom = null;
                int manifestRegionIdx = 0;
                string line;
                string[] toks;
                while ((line = reader.ReadLine()) != null)
                {
                    toks = line.Split('\t');
                    string chrom = toks[0];
                    int start = int.Parse(toks[1]) + 1; // 1-based, inclusive
                    int end = int.Parse(toks[2]); // 1-based, inclusive
                    if (chrom != currentChrom)
                    {
                        currentChrom = chrom;
                        manifestRegionIdx = 0;
                    }
                    if (!manifestRegionsByChrom.ContainsKey(currentChrom)) { continue; }
                    while (manifestRegionIdx < manifestRegionsByChrom[currentChrom].Count
                        && manifestRegionsByChrom[currentChrom][manifestRegionIdx].End < start) // |- manifest region -| |- bin -|
                    {
                        manifestRegionIdx++;
                    }
                    if (manifestRegionIdx >= manifestRegionsByChrom[currentChrom].Count || // |- last manifest region -| |- bin -|
                        end < manifestRegionsByChrom[currentChrom][manifestRegionIdx].Start) // |- bin -| |- manifest region -|
                    {
                        continue; // skip bin
                    }

                    // |- bin -|
                    //       |- manifest region -|
                    while (manifestRegionIdx < manifestRegionsByChrom[currentChrom].Count &&
                        end >= manifestRegionsByChrom[currentChrom][manifestRegionIdx].Start)
                    {
                        // calculate intersection
                        int intersectionStart = Math.Max(start, manifestRegionsByChrom[currentChrom][manifestRegionIdx].Start); // 1-based, inclusive
                        int intersectionEnd = Math.Min(end, manifestRegionsByChrom[currentChrom][manifestRegionIdx].End); // 1-based, inclusive
                                                                                                                          // start/end in BED format
                        toks[1] = String.Format("{0}", intersectionStart - 1); // 0-based, inclusive
                        toks[2] = String.Format("{0}", intersectionEnd); // 0-based, exclusive

                        // write intersected bin
                        writer.WriteLine(String.Join("\t", toks));

                        manifestRegionIdx++;
                    }
                }
            }

            return partitionedPath;
        }

        /// <summary>
        /// Invoke CanvasSNV.  Return null if this fails and we need to abort CNV calling for this sample.
        /// </summary>
        protected void InvokeCanvasSnv(CanvasCallset callset)
        {
            List<UnitOfWork> jobList = new List<UnitOfWork>();
            string canvasExecutablePath = Path.Combine(this._canvasFolder, "CanvasSNV.exe");
            string executablePath = Utilities.GetMonoPath();
            List<string> outputPaths = new List<string>();
            GenomeMetadata genomeMetadata = callset.GenomeMetadata;

            string tumorBamPath = callset.Bam.BamFile.FullName;
            string normalVcfPath = callset.NormalVcfPath.FullName;
            foreach (SequencingFiles.GenomeMetadata.SequenceMetadata chromosome in genomeMetadata.Sequences)
            {
                // Only invoke for autosomes + allosomes;
                // don't invoke it for mitochondrial chromosome or extra contigs or decoys
                if (chromosome.Type != GenomeMetadata.SequenceType.Allosome && !chromosome.IsAutosome())
                    continue;

                UnitOfWork job = new UnitOfWork();
                string outputPath = Path.Combine(callset.TempFolder, string.Format("{0}-{1}.SNV.txt.gz", chromosome.Name, callset.Id));
                outputPaths.Add(outputPath);
                job.CommandLine = string.Format("{0} {1} {2} {3} {4}", canvasExecutablePath, chromosome.Name, normalVcfPath, tumorBamPath, outputPath);
                job.ExecutablePath = executablePath;
                job.LoggingFolder = _workManager.LoggingFolder.FullName;
                job.LoggingStub = string.Format("CanvasSNV-{0}-{1}", callset.Id, chromosome.Name);
                jobList.Add(job);
            }
            Console.WriteLine("Invoking {0} processor jobs...", jobList.Count);

            // Invoke CanvasSNV jobs:
            Console.WriteLine(">>>CanvasSNV start...");
            _workManager.DoWorkParallelThreads(jobList);
            Console.WriteLine(">>>CanvasSNV complete!");

            // Concatenate CanvasSNV results:
            using (GzipWriter writer = new GzipWriter(callset.VfSummaryPath))
            {
                bool headerWritten = false;
                foreach (string outputPath in outputPaths)
                {
                    if (!File.Exists(outputPath))
                    {
                        Console.WriteLine("Error: Expected output file not found at {0}", outputPath);
                        continue;
                    }
                    using (GzipReader reader = new GzipReader(outputPath))
                    {
                        while (true)
                        {
                            string fileLine = reader.ReadLine();
                            if (fileLine == null) break;
                            if (fileLine.Length > 0 && fileLine[0] == '#')
                            {
                                if (headerWritten) continue;
                                headerWritten = true;
                            }
                            writer.WriteLine(fileLine);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Germline workflow:
        /// - Run CanvasBin, CanvasClean, CanvasPartition, CanvasDiploidCaller
        /// 
        /// Somatic workflow:
        /// - Run CanvasBin, CanvasClean, CanvasPartition, CanvasSNV, CanvasSomaticCaller
        /// </summary>
        public void CallSample(CanvasCallset callset)
        {
            Directory.CreateDirectory(callset.TempFolder);
            string canvasReferencePath = callset.KmerFasta.FullName;
            string canvasBedPath = callset.FilterBed.FullName;
            if (!File.Exists(canvasReferencePath))
            {
                throw new ApplicationException(string.Format("Error: Missing reference fasta file required for CNV calling at '{0}'", canvasReferencePath));
            }
            if (!File.Exists(canvasBedPath))
            {
                throw new ApplicationException(string.Format("Error: Missing filter bed file required for CNV calling at '{0}'", canvasBedPath));
            }

            // Prepare ploidy file:
            GenomeMetadata genomeMetadata = callset.GenomeMetadata;
            string ploidyBedPath = callset.PloidyBed?.FullName;

            // CanvasBin:
            string binnedPath = InvokeCanvasBin(callset, canvasReferencePath, canvasBedPath, ploidyBedPath);
            if (string.IsNullOrEmpty(binnedPath)) return;

            // CanvasClean:
            StringBuilder commandLine = new StringBuilder();
            commandLine.Length = 0;
            string executablePath = Path.Combine(_canvasFolder, "CanvasClean.exe");
            if (Utilities.IsThisMono())
            {
                commandLine.AppendFormat("{0} ", executablePath);
                executablePath = Utilities.GetMonoPath();
            }
            commandLine.AppendFormat("-i \"{0}\" ", binnedPath);
            string cleanedPath = Path.Combine(callset.TempFolder, string.Format("{0}.cleaned", callset.Id));
            commandLine.AppendFormat("-o \"{0}\" ", cleanedPath);
            commandLine.AppendFormat("-g");

            string ffpePath = null;

            // TruSight Cancer has 1,737 targeted regions. The cut-off 2000 is somewhat arbitrary.
            // TruSignt One has 62,309 targeted regions.
            // Nextera Rapid Capture v1.1 has 411,513 targeted regions.
            if (!callset.IsEnrichment || callset.Manifest.Regions.Count > 2000)
            {
                ffpePath = Path.Combine(callset.TempFolder, "FilterRegions.txt");
                commandLine.AppendFormat(" -s -r -f \"{0}\"", ffpePath);
            }
            if (callset.IsEnrichment) // manifest
            {
                if (!File.Exists(callset.TempManifestPath)) { NexteraManifestUtils.WriteNexteraManifests(callset.Manifest, callset.TempManifestPath); }
                commandLine.AppendFormat(" -t \"{0}\"", callset.TempManifestPath);
            }
            UnitOfWork cleanJob = new UnitOfWork()
            {
                ExecutablePath = executablePath,
                LoggingFolder = _workManager.LoggingFolder.FullName,
                LoggingStub = Path.GetFileName(cleanedPath),
                CommandLine = commandLine.ToString()
            };
            if (_customParameters.ContainsKey("CanvasClean"))
            {
                cleanJob.CommandLine = Utilities.MergeCommandLineOptions(cleanJob.CommandLine, _customParameters["CanvasClean"], true);
            }
            _workManager.DoWorkSingleThread(cleanJob);

            ////////////////////////////////////////////////////////
            // CanvasPartition:
            commandLine.Length = 0;
            executablePath = Path.Combine(_canvasFolder, "CanvasPartition.exe");
            if (Utilities.IsThisMono())
            {
                commandLine.AppendFormat("{0} ", executablePath);
                executablePath = Utilities.GetMonoPath();
            }
            commandLine.AppendFormat("-i \"{0}\" ", cleanedPath);
            commandLine.AppendFormat("-b \"{0}\" ", canvasBedPath);
            string partitionedPath = Path.Combine(callset.TempFolder, string.Format("{0}.partitioned", callset.Id));
            commandLine.AppendFormat("-o \"{0}\" ", partitionedPath);
            if (!_isSomatic)
                commandLine.AppendFormat(" -g");

            UnitOfWork partitionJob = new UnitOfWork()
            {
                ExecutablePath = executablePath,
                LoggingFolder = _workManager.LoggingFolder.FullName,
                LoggingStub = Path.GetFileName(partitionedPath),
                CommandLine = commandLine.ToString()
            };
            _workManager.DoWorkSingleThread(partitionJob);

            ////////////////////////////////////////////////////////
            // CanvasSNV
            // Prepare and run CanvasSNV jobs.  First create list of jobs:
            InvokeCanvasSnv(callset);

            ////////////////////////////////////////////////////////
            // Variant calling:
            if (callset.IsEnrichment)
            {
                partitionedPath = IntersectBinsWithTargetedRegions(callset, partitionedPath); // Intersect bins with manifest
            }

            if (_isSomatic)
            {
                RunSomaticCalling(partitionedPath, callset, canvasBedPath, ploidyBedPath, ffpePath);
            }
            else
            {
                RunGermlineCalling(partitionedPath, callset, ploidyBedPath);
            }
        }

        protected void RunSomaticCalling(string partitionedPath, CanvasCallset callset, string canvasBedPath,
            string ploidyBedPath, string ffpePath)
        {

            // get somatic SNV output:
            string somaticSnvPath = callset.SomaticVcfPath?.FullName;

            // Prepare and run CanvasSomaticCaller job:
            UnitOfWork callerJob = new UnitOfWork();
            var cnvVcfPath = callset.OutputVcfPath;
            callerJob.ExecutablePath = Utilities.GetMonoPath();
            string executablePath = Path.Combine(this._canvasFolder, "CanvasSomaticCaller.exe");
            callerJob.CommandLine = string.Format(executablePath);
            callerJob.CommandLine += string.Format(" -v {0}", callset.VfSummaryPath);
            callerJob.CommandLine += string.Format(" -i {0}", partitionedPath);
            callerJob.CommandLine += string.Format(" -o {0}", cnvVcfPath);
            callerJob.CommandLine += string.Format(" -b {0}", canvasBedPath);
            if (!string.IsNullOrEmpty(ploidyBedPath))
                callerJob.CommandLine += string.Format(" -p {0}", ploidyBedPath);
            callerJob.CommandLine += string.Format(" -n {0}", callset.SampleName);
            if (callset.IsEnrichment)
                callerJob.CommandLine += string.Format(" -e");
            if (callset.IsDbSnpVcf) // a dbSNP VCF file is used in place of the normal VCF file
                callerJob.CommandLine += string.Format(" -d");
            // get localSD metric:
            if (!string.IsNullOrEmpty(ffpePath))
            {
                // Sanity-check: CanvasClean does not always write this file. 
                // If it's not present, just carry on:
                if (File.Exists(ffpePath))
                {
                    callerJob.CommandLine += string.Format(" -f \"{0}\"", ffpePath);
                }
                else
                {
                    _logger.Info("Note: SD file not found at '{0}'", ffpePath);
                }
            }

            if (!string.IsNullOrEmpty(somaticSnvPath))
                callerJob.CommandLine += string.Format(" -s {0}", somaticSnvPath);
            callerJob.CommandLine += string.Format(" -r \"{0}\" ", callset.WholeGenomeFastaFolder);
            callerJob.LoggingFolder = _workManager.LoggingFolder.FullName;
            callerJob.LoggingStub = string.Format("SomaticCNV-{0}", callset.Id);
            _workManager.DoWorkSingleThread(callerJob);
        }

        protected void RunGermlineCalling(string partitionedPath, CanvasCallset callset, string ploidyBedPath)
        {
            StringBuilder commandLine = new StringBuilder();
            ////////////////////////////////////////////////////////
            // CanvasDiploidCaller:
            commandLine.Length = 0;
            string executablePath = Path.Combine(_canvasFolder, "CanvasDiploidCaller.exe");
            if (Utilities.IsThisMono())
            {
                commandLine.AppendFormat("{0} ", executablePath);
                executablePath = Utilities.GetMonoPath();
            }
            commandLine.AppendFormat("-i \"{0}\" ", partitionedPath);
            commandLine.AppendFormat("-v \"{0}\" ", callset.VfSummaryPath);
            var cnvVcfPath = callset.OutputVcfPath;
            commandLine.AppendFormat("-o \"{0}\" ", cnvVcfPath);
            commandLine.AppendFormat("-n \"{0}\" ", callset.SampleName);
            commandLine.AppendFormat("-r \"{0}\" ", callset.WholeGenomeFastaFolder);
            if (!string.IsNullOrEmpty(ploidyBedPath))
            {
                commandLine.AppendFormat("-p \"{0}\" ", ploidyBedPath);
            }
            if (callset.IsDbSnpVcf) // a dbSNP VCF file is used in place of the normal VCF file
                commandLine.AppendFormat("-d ");
            UnitOfWork callJob = new UnitOfWork()
            {
                ExecutablePath = executablePath,
                LoggingFolder = _workManager.LoggingFolder.FullName,
                LoggingStub = cnvVcfPath.Name,
                CommandLine = commandLine.ToString()
            };
            _workManager.DoWorkSingleThread(callJob);
        }
    }
}
