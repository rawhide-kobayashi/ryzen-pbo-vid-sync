using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using RyzenPBOVIDSync.HWiNFO;
using ZenStates.Core;
using System.Text.RegularExpressions;
using System.Security.Principal;

namespace RyzenPBOVIDSync
{
    class Program
    {
        private static readonly Cpu ryzen;

        static Program()
        {
            try
            {
                ryzen = new Cpu();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
        }
        private static readonly HWiNFOWrapper hwinfo = new();

        private static MemoryMappedFile? mmf;

        private static readonly ProcessStartInfo ycruncherInfo = new()
        {
            FileName = "y-cruncher\\y-cruncher.exe",
            Arguments = "config y-cruncher\\test.cfg",
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        private static readonly Process ycruncher = new() { StartInfo = ycruncherInfo};
        private static readonly string ycruncherConfigTemplate = @"
{{
    Action : ""StressTest""
    StressTest : {{
        AllocateLocally : true
        LogicalCores : [""0-{0}""] 
        TotalMemory : 12318351360
        SecondsPerTest : 120
        SecondsTotal : 0
        StopOnError : true
        Tests : [""{1}""] 
    }}
}}";
        static int Main()
        {
            if (!IsAdministrator())
            {
                Console.WriteLine("This application must be run as an administrator.");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }

            Console.WriteLine("Welcome to the Ryzen PBO VID synchronizer!");
            Console.WriteLine("This tool depends on y-cruncher (Alexander Yee) and ZenStates-Core (irusanov) to function.");
            Console.WriteLine("It couldn't exist without their contributions to free software.");
            Console.WriteLine("Your CPU is going to get *HOT*. This is fine. It is designed to do this. Do not be alarmed by any thermal throttling you may observe in HWiNFO.");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();
            Console.WriteLine("Setting PBO offset to 0 on all cores...");
            ryzen.SetPsmMarginAllCores(0);

            try
            {
                mmf = MemoryMappedFile.OpenExisting(HWiNFOWrapper.HWiNFO_SENSORS_MAP_FILE_NAME2, MemoryMappedFileRights.Read);
            }
            catch (Exception Ex)
            {
                Console.WriteLine("An error occured while opening the HWiNFO shared memory! - " + Ex.Message);
                Console.WriteLine("Most likely, it is not enabled. Please make sure it is enabled, and try again.");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
                Environment.Exit(1);
            }

            (var SensorIndexes, uint PPTIndex, uint AvgEffectiveClock) = GetSensorIndexes();
            Console.WriteLine($"Detected {SensorIndexes.Count} physical cores on your system.");

            List<int> PBOOffsets = [];

            Console.WriteLine("Starting undervolting routine...");

            int ycruncherThreadCount = (SensorIndexes.Count * 2) - 1;

            StartYcruncher("BKT", ycruncherThreadCount);

            for (int i = 0; i < SensorIndexes.Count; i++)
            {
                PBOOffsets.Add(0);
            }

            while (true)
            {   uint iterations = 0;
                double[] CoreVIDs = new double[SensorIndexes.Count];
                long StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - StartTime <= 1000)
                {
                    for (int i = 0; i < SensorIndexes.Count; i++) CoreVIDs[i] += GetSensorReading(SensorIndexes[i]["VID"]);
                    Thread.Sleep(200);
                    iterations++;
                }

                List<double> CoreAvgVIDs = [];

                for (int i = 0; i < SensorIndexes.Count; i++) CoreAvgVIDs.Add(CoreVIDs[i] / iterations);
                
                // 0.004 is chosen as that is the best-case minimum delta that I have seen.
                if (CoreAvgVIDs.Max() - CoreAvgVIDs.Min() <= 0.004) break;
                
                else
                {
                    int HighestIndex = GetHighestVIDIndex(CoreAvgVIDs);
                    PBOOffsets[HighestIndex] -= 1;
                    Console.WriteLine($"Worst VID delta: {CoreAvgVIDs.Max() - CoreAvgVIDs.Min():F3}");
                    ApplyPBOOffset($"{HighestIndex}:{PBOOffsets[HighestIndex]}");
                }

                // Give the values a chance to settle after changing...
                Thread.Sleep(100);
            }

            Console.WriteLine("Undervolting completed, gathering clock speed data...");

            var NewBKTClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var NewBKTPPTAvg = GetAvgSensorOverPeriod(PPTIndex, 10000);

            ycruncher.Kill(true);
            StartYcruncher("BBP", ycruncherThreadCount);

            var NewBBPClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var NewBBPPPTAvg = GetAvgSensorOverPeriod(PPTIndex, 10000);

            ryzen.SetPsmMarginAllCores(0);

            ycruncher.Kill(true);
            StartYcruncher("BKT", ycruncherThreadCount);

            var StockBKTClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var StockBKTPPTAvg = GetAvgSensorOverPeriod(PPTIndex, 10000);

            ycruncher.Kill(true);
            StartYcruncher("BBP", ycruncherThreadCount);

            var StockBBPClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);
            var StockBBPPTTAvg = GetAvgSensorOverPeriod(PPTIndex, 10000);

            ycruncher.Kill(true);

            for (int i = 0; i < SensorIndexes.Count; i++) ApplyPBOOffset($"{i}:{PBOOffsets[i]}");

            Console.WriteLine($"Original Scalar multicore average: {StockBKTClockAvg:F2}");
            Console.WriteLine($"Original AVX2|512 multicore average: {StockBBPClockAvg:F2}");
            Console.WriteLine($"New Scalar multicore average: {NewBKTClockAvg:F2}");
            Console.WriteLine($"New AVX2|512 multicore average: {NewBBPClockAvg:F2}");

            Console.WriteLine($"Improved average multicore clock speed in Scalar workloads by {NewBKTClockAvg / StockBKTClockAvg:F2}x above baseline.");
            Console.WriteLine($"Improved average multicore clock speed in AVX2|512 workloads by {NewBBPClockAvg / StockBBPClockAvg:F2}x above baseline.");
            Console.WriteLine($"Original scalar efficiency: {StockBKTClockAvg / StockBKTPPTAvg:F2} MHz/w");
            Console.WriteLine($"Original AVX2|512 efficiency: {StockBBPClockAvg / StockBBPPTTAvg:F2} MHz/w");
            Console.WriteLine($"New scalar efficiency: {NewBKTClockAvg / NewBKTPPTAvg:F2} MHz/w");
            Console.WriteLine($"New AVX2|512 efficiency: {NewBBPClockAvg / NewBBPPPTAvg:F2} MHz/w");

            Console.WriteLine("Enter these settings into your BIOS to make this configuration permanent.");
            Console.WriteLine("Final PBO settings:");

            for (int i = 0; i < SensorIndexes.Count; i++) Console.WriteLine($"Core {i}: {PBOOffsets[i]}");

            Console.WriteLine("Thank you for using Ryzen PBO VID Sync.");
            Console.WriteLine("rawhide kobayashi - https://blog.neet.works/");

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();

            ycruncher.Dispose();
            ryzen.Dispose();
            mmf.Dispose();

            return 0;
        }

        private static void StartYcruncher(string testType, int ycruncherThreadCount)
        {
            File.WriteAllText("y-cruncher\\test.cfg", string.Format(ycruncherConfigTemplate, Convert.ToString(ycruncherThreadCount), testType));
            ycruncher.Start();
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static List<int> MapUnavailableCores()
        {
            List<int> unavailableCores = [];
            if (ryzen.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                uint cores = ryzen.info.topology.physicalCores;
                for (var i = 0; i < cores; i++)
                {
                    int mapIndex = i < 8 ? 0 : 1;
                    if ((~ryzen.info.topology.coreDisableMap[mapIndex] >> i % 8 & 1) == 0) unavailableCores.Add(i);
                }
            }

            return unavailableCores;
        }

        private static int GetHighestVIDIndex(List<double> VIDs)
        {
            int HighestIndex = 0;
            for (int i = 1; i < VIDs.Count; i++)
            {
                if (VIDs[i] > VIDs[HighestIndex]) HighestIndex = i;
            }

            return HighestIndex;
        }

        private static (Dictionary<int, Dictionary<string, uint>>, uint, uint) GetSensorIndexes()
        {
            uint numSensors;
            uint numReadingElements;
            uint offsetSensorSection;
            uint sizeSensorElement;
            uint offsetReadingSection;
            uint sizeReadingSection;
            List<uint> vid_indexes = [];
            List<uint> mhz_indexes = [];
            List<string> masterSensorNames = [];
            Dictionary<int, Dictionary<string, uint>> SensorIndexes = [];
            uint PPTIndex = 0;
            uint AvgEffectiveClock = 0;

            using (var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2)), MemoryMappedFileAccess.Read))
            {
			    HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2 HWiNFOMemory ;
                accessor.Read(0, out HWiNFOMemory);
			    numSensors = HWiNFOMemory.dwNumSensorElements ;
                numReadingElements = HWiNFOMemory.dwNumReadingElements;
			    offsetSensorSection = HWiNFOMemory.dwOffsetOfSensorSection ;
                sizeSensorElement = HWiNFOMemory.dwSizeOfSensorElement;
                offsetReadingSection = HWiNFOMemory.dwOffsetOfReadingSection;
                sizeReadingSection = HWiNFOMemory.dwSizeOfReadingElement;

			    for (uint dwSensor = 0; dwSensor < numSensors; dwSensor ++)
                {
                    using var sensor_element_accessor = mmf.CreateViewStream(offsetSensorSection + (dwSensor * sizeSensorElement), sizeSensorElement, MemoryMappedFileAccess.Read);
                    byte[] byteBuffer = new byte[sizeSensorElement];
                    sensor_element_accessor.Read(byteBuffer, 0, (int)sizeSensorElement);
                    GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                    HWiNFOWrapper._HWiNFO_SENSORS_SENSOR_ELEMENT SensorElement = (HWiNFOWrapper._HWiNFO_SENSORS_SENSOR_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                        typeof(HWiNFOWrapper._HWiNFO_SENSORS_SENSOR_ELEMENT));
                    handle.Free();

                    masterSensorNames.Add(SensorElement.szSensorNameUser);
                }
                for (uint dwReading = 0; dwReading < numReadingElements; dwReading++)
                {
                    using var sensor_element_accessor = mmf.CreateViewStream(offsetReadingSection + (dwReading * sizeReadingSection), sizeReadingSection, MemoryMappedFileAccess.Read);
                    byte[] byteBuffer = new byte[sizeReadingSection];
                    sensor_element_accessor.Read(byteBuffer, 0, (int)sizeReadingSection);
                    GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
                    HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT ReadingElement = (HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                        typeof(HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT));
                    handle.Free();

                    Match match = Regex.Match(ReadingElement.szLabelOrig, @"(?<core_vid>Core [0-9]* VID)|(?<core_mhz>Core [0-9]* T0 Effective Clock)|(?<ppt>^CPU PPT$)|(?<avg_eff_clock>Average Effective Clock)");

                    if (match.Success)
                    {
                        if (match.Groups["core_vid"].Success) vid_indexes.Add(dwReading);
                        else if (match.Groups["core_mhz"].Success) mhz_indexes.Add(dwReading);
                        else if (match.Groups["ppt"].Success) PPTIndex = dwReading;
                        else if (match.Groups["avg_eff_clock"].Success) AvgEffectiveClock = dwReading;
                    }
                }
            }

            for (int i = 0; i < vid_indexes.Count; i++)
            {
                SensorIndexes.Add(i, new Dictionary<string, uint>());
                SensorIndexes[i]["VID"] = vid_indexes[i];
                SensorIndexes[i]["MHz"] = mhz_indexes[i];
            }

            return (SensorIndexes, PPTIndex, AvgEffectiveClock);
        }

        private static double GetSensorReading(uint index)
        {
            uint offsetReadingSection;
            uint sizeReadingSection;

            using var accessor = mmf.CreateViewAccessor(0, Marshal.SizeOf(typeof(HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2)), MemoryMappedFileAccess.Read);
            HWiNFOWrapper._HWiNFO_SENSORS_SHARED_MEM2 HWiNFOMemory;
            accessor.Read(0, out HWiNFOMemory);
            offsetReadingSection = HWiNFOMemory.dwOffsetOfReadingSection;
            sizeReadingSection = HWiNFOMemory.dwSizeOfReadingElement;

            using var sensor_element_accessor = mmf.CreateViewStream(offsetReadingSection + (index * sizeReadingSection), sizeReadingSection, MemoryMappedFileAccess.Read);
            byte[] byteBuffer = new byte[sizeReadingSection];
            sensor_element_accessor.Read(byteBuffer, 0, (int)sizeReadingSection);
            GCHandle handle = GCHandle.Alloc(byteBuffer, GCHandleType.Pinned);
            HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT ReadingElement = (HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT)Marshal.PtrToStructure(handle.AddrOfPinnedObject(),
                typeof(HWiNFOWrapper._HWiNFO_SENSORS_READING_ELEMENT));
            handle.Free();

            return ReadingElement.Value;
        }

        private static double GetAvgSensorOverPeriod(uint SensorIndex, uint PeriodMS)
        {
            long StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            List<double> SensorReadings = [];

            while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - StartTime <= 5000)
            {
                SensorReadings.Add(GetSensorReading(SensorIndex));
                Thread.Sleep(200);
            }

            double SumSensorReadings = 0;

            for (int i = 0; i < SensorReadings.Count; i++)
            {
                SumSensorReadings += SensorReadings[i];
            }

            return SumSensorReadings / SensorReadings.Count;
        }

        private static void ApplyPBOOffset(string offsetArgs)
        {
            string[] arg = offsetArgs.Split(',');
            var unavailableCoreMap = MapUnavailableCores();
            int coreOffset = 0;

            for (int i = 0; i < arg.Length; i++) 
            {
                int core = Convert.ToInt32(arg[i].Split(':')[0]);
                int offset = Convert.ToInt32(arg[i].Split(':')[1]);
                
                // This is a user-oriented feature to allow you to input cores sequentially instead of mapping out
                // the disabled cores yourself. I do not have the hardware available to test it.
                if (unavailableCoreMap.Contains(core + coreOffset)) coreOffset += 1;

                // Magic numbers from SMUDebugTool
                // This does some bitshifting calculations to get the mask for individual cores for chips with up to two CCDs
                // I'm not sure if it would work with more, in theory. It's unclear to me based on the github issues.
                int mapIndex = core < 8 ? 0 : 1;
                if ((~ryzen.info.topology.coreDisableMap[mapIndex] >> core % 8 & 1) == 1)
                {
                    ryzen.SetPsmMarginSingleCore((uint)(((mapIndex << 8) | core % 8 & 0xF) << 20), offset);
                    Console.WriteLine($"Set core {core} offset to {offset}!");
                }

                else
                {
                    Console.WriteLine($"Unable to set offset on disabled core {core}.");
                }
            }
        }
    }
}
