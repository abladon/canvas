Canvas Copy Number Variant Caller
=================================

Canvas is a tool for calling copy number variants (CNVs) from human DNA sequencing data.  It can work either with germline data, or paired tumor/normal samples.  Its primary input is aligned reads (in .bam format), and its primary output is a report (in a .vcf file) giving the copy number status of the genome.  

Canvas is used as the copy number caller in the Isaac Whole Genome Sequencing workflow in BaseSpace (https://basespace.illumina.com), and in HiSeq Analysis Software (HAS) (http://support.illumina.com/sequencing/sequencing_software/hiseq-analysis-software.html).  

Canvas is written in C# and runs either under a recent version of Mono (e.g. 3.10.0) or on .NET 4.5.1.

For more information on Canvas, see the [software design description] [SDD] for a description of Canvas and the algorithms it uses.

[SDD]:Docs/CanvasSoftwareDesignDescription.pdf

License
-------

Copyright (c) 2013-2015 Illumina, Inc. All rights reserved.

This software is provided under the terms and conditions of the GNU GENERAL PUBLIC LICENSE Version 3

You should have received a copy of the GNU GENERAL PUBLIC LICENSE Version 3 along with this program. If not, see https://github.com/illumina/licenses/.

Canvas includes several third party packages provided under other open source licenses, please see [COPYRIGHT.txt] (COPYRIGHT.txt) for additional details.

Build instructions
------------------

### Binaries:
It is recmomended to start from one of the [binary distributions on the Canvas releases page] [releases] if a suitable version is available.  

[releases]:https://github.com/Illumina/canvas/releases

### Source code organization:
Canvas consists of serveral components all built from one solution file (Canvas.sln).  There are several executables - e.g. CanvasBin counts coverage for each bin, CanvasSomaticCaller makes CNV calls for tumor/normal data - as well as some shared libraries with utility functions (math functions, file I/O for various formats, etc.)  

### Compiling from source
Open the solution file (Canvas.sln) using Visual Studio 2013, and build the main solution configuration (x64 + Release).  The managed code can be run on a Windows system or on a Linux system using Mono.  The FileCompression library (unmanaged c++ code) can be rebuilt from source under Linux, or the prebuilt binary libFileCompression.so can be used.

### Operating System Guidelines

#### Linux
Canvas is known to run under the following Linux distributions using Mono 3.10.0:
- CentOS 5, 6

Other Linux distributions and other recent Mono versions are likely to work as well but have not been explicitly tested

#### Windows
Canvas is known to run on Windows 7 or Windows 8 systems using .NET 4.5.1

Run instructions
------------------
Canvas can be run on a variety of sequencing input. See the help information from the command line executable for the supported workflows and required input files:

$Canvas.exe --help  
Canvas 1.3.4.0 Copyright c Illumina 2015  
Usage: Canvas.exe [MODE] [OPTIONS]+  
Available modes:  
        Germline-WGS - CNV calling of a germline sample from whole genome sequencing data  
        Somatic-Enrichment - CNV calling of a somatic sample from targeted sequencing data  
        Somatic-WGS - CNV calling of a somatic sample from whole genome sequencing data  
        Tumor-normal-enrichment - CNV calling of a tumor/normal pair from targeted sequencing data  
Options:  
  -h, --help                 show this message and exit  
  -v, --version              print version and exit  

#### Reference genome
The required input files for Human reference genome builds GRCh37, hg19, and GRCh38 can be downloaded from https://illumina.box.com/CanvasPublic. When using a custom reference genome the equivalent files need to be created. Use the FlagUniqueKmers project to generate the annotated fasta file (kmer.fa) for a custom reference genome. 
