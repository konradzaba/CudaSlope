using Alea;
using BumpKit;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CudaSlope
{
    class Program
    {
        const string GdalInfo = "GdalInfo";
        const string GdalTranslate = "GdalTranslate";
        const string HeaderHeightName = "height=";
        const string HeaderWidthName = "width=";
        const float RadiansToDegrees = 180.0f / (float)Math.PI;

        static int _dataPortionSize;
        static float[,] _slope;

        static string _inputPath, _outputPath;
        static bool _isGpuModeEnabled = false;
        static int _iterations = 1;
        static GisFileStats _fileStats;

        static void Main(string[] args)
        {
            var argumentInfo = ProcessArguments(args);
            if (argumentInfo.Any())
            {
                Console.Write(argumentInfo);
            }
            else
            {
                string xyzFile = _inputPath;
                if (!_inputPath.ToUpper().EndsWith("XYZ"))
                {
                    Console.WriteLine("Converting to readable form using GDAL...");
                    xyzFile = TransformToTextFile(_inputPath);
                }

                Console.Write("Reading elevation points...");
                var elevation = ReadPoints(xyzFile);
                Gpu gpu = null;

                if (_isGpuModeEnabled)
                {
                    gpu = Gpu.Default;
                    _dataPortionSize = AdjustForGpuMemorySize(gpu.Context.Device.TotalMemory, _fileStats.Width);
                    Console.WriteLine($"Found GPU VRAM = {gpu.Context.Device.TotalMemory} bytes.");
                    var portions = (elevation.GetLength(0) / _dataPortionSize) + 1;
                    if (portions == 1) Console.WriteLine("No portions necessary, enough VRAM!");
                    else Console.WriteLine($"Task split into {portions} portions, not enough VRAM!");
                }

                var timeSeries = new List<long>();
                //iterations is > 1 in benchmark, otherwise = 1
                for (int i = 0; i < _iterations; i++)
                {
                    var stopWatch = new Stopwatch();
                    stopWatch.Start();
                    
                    if(_isGpuModeEnabled)
                        CalculateSlopeGpu(gpu, elevation);
                    else
                        CalculateSlopeCpu(_fileStats, elevation);

                    stopWatch.Stop();
                    Console.WriteLine($"Calculations took {stopWatch.ElapsedMilliseconds} ms");
                    timeSeries.Add(stopWatch.ElapsedMilliseconds);
                }
                if (_iterations > 1)
                    Console.WriteLine($"Average = {timeSeries.Average()} ms");

                Console.WriteLine("Normalizing slope for gradient & exporting resulting image...");
                NormalizeAndExport(_fileStats, _outputPath);

                if (!_inputPath.ToUpper().EndsWith("XYZ"))
                {
                    Console.WriteLine("Deleting generated XYZ file...");
                    File.Delete(xyzFile);
                }
                Console.WriteLine("Finished.");
            }
        }

        #region processing arguments, printing usage

        /// <summary>
        /// Processes the arguments passed to the executable.
        /// Prints relevant information if necessary.
        /// </summary>
        /// <param name="args">Array of passed arguments</param>
        /// <returns>Info message if parsing failed, empty string otherwise.</returns>
        private static string ProcessArguments(string[] args)
        {
            var outputInfo = new List<string>();
            if (!args.Any()) return PrintUsage();
            foreach(var arg in args)
            {
                if (arg.StartsWith("-i"))
                {
                    _inputPath = arg.Substring(3);
                }
                else if (arg.StartsWith("-o"))
                {
                    _outputPath = arg.Substring(3);
                }
                else if (arg.StartsWith("-m"))
                {
                    _isGpuModeEnabled = arg.ToUpper().Contains("GPU");
                }
                else if (arg.StartsWith("-b"))
                {
                    _iterations = int.Parse(arg.Substring(3));
                }
            }

            if (_inputPath.Length == 0) outputInfo.Add("Missing input parameter");
            if (_outputPath.Length == 0) outputInfo.Add("Missing output parameter");

            return string.Join(Environment.NewLine, outputInfo);
        }

        /// <summary>
        /// Returns info text from resource file.
        /// </summary>
        private static string PrintUsage()
        {
            return Properties.Resources.usageInfo;
        }
        #endregion

        #region transform to readable file using GDAL

        /// <summary>
        /// Uses GDAL in order to transform input file to readable text file.
        /// </summary>
        /// <param name="inputFile">Path to input file that is meant to be parsed.</param>
        /// <returns>Returns the path to the output file.</returns>
        static string TransformToTextFile(string inputFile)
        {
            string outputFile = $"{Guid.NewGuid().ToString()}.xyz";
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ConfigurationManager.AppSettings[GdalTranslate],
                    Arguments = $"--config GDAL_CACHEMAX 50% -of XYZ {inputFile} {outputFile}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            proc.OutputDataReceived += (sender, e) => Console.Write(e.Data);
            proc.BeginOutputReadLine();
            proc.WaitForExit();

            //append file stats at the end
            var stats = ReadStatistics(inputFile);
            File.AppendAllLines(outputFile, new[] { $"# {HeaderHeightName}{stats.Height} {HeaderWidthName}{stats.Width}" });

            return outputFile;
        }

        /// <summary>
        /// Uses GDAL to read basic file statistics necessary for the processing.
        /// </summary>
        /// <param name="path">Path to the input file.</param>
        /// <returns>Basic file statistics.</returns>
        static GisFileStats ReadStatistics(string path)
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ConfigurationManager.AppSettings[GdalInfo],
                    Arguments = $" -json -stats {path}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            dynamic statsJson = JsonConvert.DeserializeObject(output);
            return new GisFileStats
            {
                Height = int.Parse(statsJson.size[0].ToString()),
                Width = int.Parse(statsJson.size[1].ToString())
            };
        }
        #endregion

        #region read elevation points one by one from readable file
        /// <summary>
        /// Reads the points from XYZ text file and returns a matrix of elevation.
        /// </summary>
        /// <param name="xyzFile">Path to XYZ text file.</param>
        /// <returns>Matrix of elevation points.</returns>
        private static float[,] ReadPoints(string xyzFile)
        {
            //first read stats
            var statsLine = File.ReadLines(xyzFile).Last();
            _fileStats = new GisFileStats();

            foreach (var stat in statsLine.Split(' '))
            {
                if (stat.StartsWith(HeaderHeightName))
                    _fileStats.Height = int.Parse(stat.Substring(HeaderHeightName.Length));
                else if (stat.StartsWith(HeaderWidthName))
                    _fileStats.Width = int.Parse(stat.Substring(HeaderWidthName.Length));
            }

            //then read whole content
            using (var reader = File.OpenText(xyzFile))
            {
                var header = reader.ReadLine();
                var toReturn = new float[_fileStats.Height, _fileStats.Width];
                string line;
                var count = 0;
                var column = 0;
                float? lastValueX = null;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("#"))
                    {
                        var split = line.Split(' ');
                        #region calculate grid spacing
                        if (_fileStats.GridSpacing == 0)
                        {
                            if (!lastValueX.HasValue)
                                lastValueX = float.Parse(split[0], CultureInfo.InvariantCulture);
                            else
                                _fileStats.GridSpacing = 8 * (float.Parse(split[0], CultureInfo.InvariantCulture) - lastValueX.Value);
                        }
                        #endregion
                        var pointElevation = float.Parse(split[2], CultureInfo.InvariantCulture);
                        if (pointElevation < 0) pointElevation = 0;
                        if (count < _fileStats.Height)
                        {
                            toReturn[count, column] = pointElevation;
                            count++;
                        }
                        else
                        {
                            column++;
                            count = 0;
                            toReturn[count, column] = pointElevation;
                            PrintProgress(column, _fileStats.Width);
                            count++;
                        }
                    }
                }
                Console.WriteLine();
                return toReturn;
            }

        }

        /// <summary>
        /// Used to print progress for time consuming process.
        /// </summary>
        /// <param name="current">Progress step</param>
        /// <param name="total">Total possible progress value.</param>
        private static void PrintProgress(int current, int total)
        {
            var portion = total / 10;
            if (current % portion == 0)
                Console.Write($"{10 * (current / portion)}%... ");
        }

        #endregion

        #region slope GPU calculations

        /// <summary>
        /// Adjust the portion size for the VRAM memory available on GPU.
        /// </summary>
        /// <param name="totalMemory">Total memory available on GPU.</param>
        /// <param name="width">Width of the input imagery.</param>
        /// <returns></returns>
        private static int AdjustForGpuMemorySize(ulong totalMemory, int width)
        {
            const long bytesGB = 1073741824;

            //value found through trial and error method
            const long sizeAllowedPerGB = 70000000;

            return (int)((sizeAllowedPerGB * (totalMemory / bytesGB)) / (ulong)width);
        }

        /// <summary>
        /// Main function for calculating the slope on GPU. Handles dividing the data into portions,
        /// launching actual computations for each portion, and copying back the result to the main data structure.
        /// If portions are not necessary - there is enough VRAM - just returns the output from function calculating slope.
        /// </summary>
        /// <param name="gpu">GPU instance.</param>
        /// <param name="elevation">Matrix of elevations.</param>
        private static void CalculateSlopeGpu(Gpu gpu, float[,] elevation)
        {
            if (_dataPortionSize > elevation.GetLength(0))
            {
                _slope = CalculateSlopePortionGpu(gpu, elevation, elevation.GetLength(0), _fileStats.Width, _fileStats.GridSpacing);
            }
            else
            {
                #region prepare portions due to GPU VRAM constraints
                var elevationPortions = new List<float[,]>();
                var count = 0;

                var elevationPortion = new float[_dataPortionSize > elevation.GetLength(0) ? elevation.GetLength(0) : _dataPortionSize, _fileStats.Width];
                var i2 = 0;
                for (int i = 0; i < _fileStats.Height; i++)
                {
                    Parallel.For(0, _fileStats.Width, j =>
                    {
                        elevationPortion[count, j] = (float)elevation[i2, j];
                    });
                    i2++;
                    count++;
                    if (count == _dataPortionSize)
                    {
                        elevationPortions.Add(elevationPortion);
                        count = 0;
                        var sizeX = _dataPortionSize;
                        if (_fileStats.Height - i < sizeX)
                            sizeX = _fileStats.Height - i - 1;
                        elevationPortion = new float[sizeX, _fileStats.Width];
                    }
                }
                elevationPortions.Add(elevationPortion);
                #endregion

                #region calculate slope using CUDA for each portion
                var resultsToCopy = new List<float[,]>();

                foreach (var portion in elevationPortions)
                {
                    resultsToCopy.Add(CalculateSlopePortionGpu(gpu, portion, portion.GetLength(0), _fileStats.Width, _fileStats.GridSpacing));
                }
                #endregion

                #region copy back to main data structure the results from portions
                var overall = 0;
                _slope = new float[_fileStats.Height, _fileStats.Width];
                foreach (var result in resultsToCopy)
                {
                    var size = result.GetLength(0);
                    for (int i = 0; i < size; i++)
                    {
                        Parallel.For(0, _fileStats.Width, j =>
                        {
                            _slope[overall, j] = result[i, j];
                        });
                        overall++;
                    }
                }
                #endregion
            }
        }

        /// <summary>
        /// The actual slope computations for GPU. Returns a matrix of slope angles. 
        /// </summary>
        /// <param name="gpu">GPU instance</param>
        /// <param name="portion">Portion of data to be processed</param>
        /// <param name="portionSize">The size of the data portion (number of height columns for input image)</param>
        /// <param name="inputImageWidth">Input image width</param>
        /// <param name="inputImageGridSpacing">Actual space between each two points on grid.</param>
        /// <returns>Matrix of slope angles.</returns>
        [GpuManaged]
        private static float[,] CalculateSlopePortionGpu(Gpu gpu, float[,] portion, int portionSize, int inputImageWidth, float inputImageGridSpacing)
        {
            var portionGpu = gpu.Allocate(portion);
            var gpuSlope = gpu.Allocate<float>(portionSize, inputImageWidth);
            for (int i = 1; i < portionSize-1; i++)
            {
                Alea.Parallel.GpuExtension.For(gpu, 1, inputImageWidth - 1, j =>
                {
                    var slopeEastWest =
                        ((portionGpu[i - 1, j - 1] + 2 * portionGpu[i, j - 1] + portionGpu[i, j + 1]) -
                        (portionGpu[i - 1, j + 1] + 2 * portionGpu[i, j + 1] + portionGpu[i + 1, j + 1])) / inputImageGridSpacing;

                    var slopeNorthSouth =
                        ((portionGpu[i - 1, j - 1] + 2 * portionGpu[i - 1, j] + portionGpu[i - 1, j + 1]) -
                        (portionGpu[i + 1, j - 1] + 2 * portionGpu[i + 1, j] + portionGpu[i + 1, j + 1])) / inputImageGridSpacing;

                    var slopePercentage = DeviceFunction.Sqrt(DeviceFunction.Pow(slopeEastWest, 2) + DeviceFunction.Pow(slopeNorthSouth, 2));

                    gpuSlope[i, j] = DeviceFunction.Atan(slopePercentage) * RadiansToDegrees;
                });
            }

            Gpu.Free(portionGpu);
            var resultSlope = Gpu.CopyToHost(gpuSlope);
            Gpu.Free(gpuSlope);
            return resultSlope;
        }
        #endregion

        #region slope CPU calculations

        /// <summary>
        /// The actual slope calculations for CPU.
        /// </summary>
        /// <param name="stats">GIS file statistics</param>
        /// <param name="elevation">Matrix of elevations</param>
        private static void CalculateSlopeCpu(GisFileStats stats, float[,] elevation)
        {
            _slope = new float[stats.Height, stats.Width];
            float gridSpacing = stats.GridSpacing;
            Parallel.For(1, stats.Height - 1, i =>
            {
                for (int j = 1; j < stats.Width - 1; j++)
                {
                    var slopeEastWest =
                        ((elevation[i - 1, j - 1] + 2 * elevation[i, j - 1] + elevation[i, j + 1]) -
                        (elevation[i - 1, j + 1] + 2 * elevation[i, j + 1] + elevation[i + 1, j + 1])) / gridSpacing;
                    var slopeNorthSouth =
                        ((elevation[i - 1, j - 1] + 2 * elevation[i - 1, j] + elevation[i - 1, j + 1]) -
                        (elevation[i + 1, j - 1] + 2 * elevation[i + 1, j] + elevation[i + 1, j + 1])) / gridSpacing;
                    var slopePercentage = Math.Sqrt(Math.Pow(slopeEastWest, 2f) + Math.Pow(slopeNorthSouth, 2f));
                    //cast performance penalty is negligible here (tested, kept for simplifying the code)
                    _slope[i, j] = (float)Math.Atan(slopePercentage) *  RadiansToDegrees;
                }
            });
        }
        #endregion

        #region normalization and export of gradient

        /// <summary>
        /// Used to colorize each pixel's color component (RGB) for gradient.
        /// </summary>
        /// <param name="value">Value for which the color is meant to be mapped.</param>
        /// <param name="lowColorComponent">Gradient color component for small values.</param>
        /// <param name="mediumColorComponent">Gradient color component for medium values.</param>
        /// <param name="highColorComponent">Gradient color component for large values.</param>
        /// <returns></returns>
        private static byte ColorizeComponent(double value, byte lowColorComponent, byte mediumColorComponent, byte highColorComponent)
        {
            if (value < 0.5)
            {
                return Convert.ToByte((mediumColorComponent * value * 2.0) + lowColorComponent * (0.5 - value) * 2.0);
            }
            else
            {
                return Convert.ToByte(highColorComponent * (value - 0.5) * 2.0 + mediumColorComponent * (1.0 - value) * 2.0);
            }
        }

        /// <summary>
        /// Performs slope normalization, applies gradient of colors and creates the output file.
        /// </summary>
        /// <param name="stats">GIS file statistics.</param>
        /// <param name="exportPath">Path for the output file.</param>
        private static void NormalizeAndExport(GisFileStats stats, string exportPath)
        {
            const double maxAngle = 45.0;

            //3-color gradient
            const byte smallR = 255;
            const byte smallG = 0;
            const byte smallB = 0;

            const byte mediumR = 255;
            const byte mediumG = 255;
            const byte mediumB = 0;

            const byte largeR = 0;
            const byte largeG = 255;
            const byte largeB = 0;

            //ensure that RAM is cleared now - following operations take a lot of memory
            GC.Collect();

            var coloredGradient = new Color[stats.Height, stats.Width];

            Parallel.For(0, stats.Height, i =>
            {
                for (int j = 0; j < stats.Width; j++)
                {
                    coloredGradient[i, j] = Color.Black;
                    if (_slope[i, j] >= 0)
                    {
                        var normalized = _slope[i, j] / maxAngle;
                        if (normalized > 1) normalized = 1;
                        var r = ColorizeComponent(normalized, largeR, mediumR, smallR);
                        var g = ColorizeComponent(normalized, largeG, mediumG, smallG);
                        var b = ColorizeComponent(normalized, largeB, mediumB, smallB);
                        coloredGradient[i, j] = Color.FromArgb(r, g, b);
                    }
                }
            });

            ExportImage(stats.Height, stats.Width, exportPath, coloredGradient);
        }

        /// <summary>
        /// Uses a library to quickly export the matrix of colors to actual file.
        /// </summary>
        /// <param name="height">Output image height.</param>
        /// <param name="width">Output image width.</param>
        /// <param name="outputPath">Path for the output image.</param>
        /// <param name="coloredGradient">Matrix of slope gradient colors.</param>
        private static void ExportImage(int height, int width, string outputPath, Color[,] coloredGradient)
        {
            try
            {
                Bitmap processedBitmap = new Bitmap(height, width);
                using (var context = processedBitmap.CreateUnsafeContext())
                {
                    for (var x = 0; x < context.Width; x++)
                        for (var y = 0; y < context.Height; y++)
                        {
                            context.SetPixel(x, y, coloredGradient[x, y]);
                        }
                }
                processedBitmap.Save(outputPath);
            }
            catch (IndexOutOfRangeException ex)
            {
                Console.WriteLine("Problem exporting image " + ex);
            }
        }
        #endregion
    }
}
