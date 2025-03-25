using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using RyzenPBOVIDSync.HWiNFO;
using ZenStates.Core;
using System.Text.RegularExpressions;

namespace RyzenPBOVIDSync
{
    class Program
    {
        private static readonly Cpu ryzen = new();
        private static readonly HWiNFOWrapper hwinfo = new();

        private static MemoryMappedFile mmf;

        static int Main()
        {
            ProcessStartInfo ycruncherBKTInfo = new ProcessStartInfo
            {
                FileName = "y-cruncher\\y-cruncher.exe",
                Arguments = "config y-cruncher\\test_bkt_forever.cfg",
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process ycruncherBKT = new Process{StartInfo = ycruncherBKTInfo};

            ProcessStartInfo ycruncherBBPInfo = new ProcessStartInfo
            {
                FileName = "y-cruncher\\y-cruncher.exe",
                Arguments = "config y-cruncher\\test_bbp_forever.cfg",
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process ycruncherBBP = new Process{StartInfo = ycruncherBBPInfo};

            Console.WriteLine("Welcome to the Ryzen PBO VID synchronizer!");
            Console.WriteLine("This tool depends on y-cruncher (Alexander Yee) and ZenStates-Core (irusanov) to function.");
            Console.WriteLine("It couldn't exist without their contributions to free software.");
            Console.WriteLine("Setting PBO offset to 0 on all cores...");
            ryzen.SetPsmMarginAllCores(0);

            try
            {
                mmf = MemoryMappedFile.OpenExisting(HWiNFOWrapper.HWiNFO_SENSORS_MAP_FILE_NAME2, MemoryMappedFileRights.Read);
            }
            catch (Exception Ex)
            {
                Console.WriteLine("An error occured while opening the HWiNFO shared memory! - " + Ex.Message);
                Console.WriteLine("Press ENTER to exit program...");
                Console.ReadLine();
                Environment.Exit(1);
            }

            (var SensorIndexes, uint PPTIndex, uint AvgEffectiveClock) = GetSensorIndexes();
            Console.WriteLine($"Detected {SensorIndexes.Count} physical cores on your system.");
            Console.WriteLine("Running a scalar stress test until the system reaches thermal equalibrium. This may take a while...");

            ycruncherBKT.Start();

            while (true)
            {
                var InitialPPT = GetSensorReading(PPTIndex);
                Thread.Sleep(55000);
                var PPTAvg = GetAvgSensorOverPeriod(PPTIndex, 5000);
                Console.WriteLine($"PPT changed by {InitialPPT - PPTAvg} over the last minute.");
                if (Math.Abs(InitialPPT - PPTAvg) <= 3) break;
            }

            Console.WriteLine("System thermally saturated. Collecting data on initial multithreaded scalar clock speeds.");

            var StockBKTClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);

            ycruncherBKT.Kill(true);
            ycruncherBBP.Start();

            Console.WriteLine("Running an AVX2|512 stress test until the system reaches thermal equalibrium. This may take a while...");

            while (true)
            {
                var InitialPPT = GetSensorReading(PPTIndex);
                Thread.Sleep(55000);
                var PPTAvg = GetAvgSensorOverPeriod(PPTIndex, 5000);
                Console.WriteLine($"PPT changed by {InitialPPT - PPTAvg} over the last minute.");
                if (Math.Abs(InitialPPT - PPTAvg) <= 3) break;
            }

            Console.WriteLine("System thermally saturated. Collecting data on initial multithreaded AVX2|512 clock speeds.");

            var StockBBPClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);

            List<int> PBOOffsets = [];

            Console.WriteLine($"Stock Scalar multicore average: {StockBKTClockAvg}");
            Console.WriteLine($"Stock AVX2|512 multicore average: {StockBBPClockAvg}");
            Console.WriteLine("Starting undervolting routine...");

            ycruncherBBP.Kill(true);
            ycruncherBKT.Start();

            Thread.Sleep(10000);

            for (int i = 0; i < SensorIndexes.Count; i++)
            {
                PBOOffsets.Add(0);
            }

            while (true)
            {   uint iterations = 0;
                double[] CoreVIDs = new double[SensorIndexes.Count];
                long StartTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                while (DateTimeOffset.Now.ToUnixTimeMilliseconds() - StartTime <= 5000)
                {
                    for (int i = 0; i < SensorIndexes.Count; i++) CoreVIDs[i] += GetSensorReading(SensorIndexes[i]["VID"]);
                    Thread.Sleep(200);
                    iterations++;
                }

                List<double> CoreAvgVIDs = [];

                for (int i = 0; i < SensorIndexes.Count; i++) CoreAvgVIDs.Add(CoreVIDs[i] / iterations);
                
                if (CoreAvgVIDs.Max() - CoreAvgVIDs.Min() <= 0.005) break;
                
                else
                {
                    int HighestIndex = GetHighestVIDIndex(CoreAvgVIDs);
                    PBOOffsets[HighestIndex] -= 1;
                    Console.WriteLine($"Worst VID delta: {CoreAvgVIDs.Max() - CoreAvgVIDs.Min()}");
                    ApplyPBOOffset($"{HighestIndex}:{PBOOffsets[HighestIndex]}");
                }

                // Give the values a chance to settle after changing...
                Thread.Sleep(2000);
            }

            Console.WriteLine("Undervolting completed, gathering new clock speed data...");

            while (true)
            {
                var InitialPPT = GetSensorReading(PPTIndex);
                Thread.Sleep(55000);
                var PPTAvg = GetAvgSensorOverPeriod(PPTIndex, 5000);
                Console.WriteLine($"PPT changed by {InitialPPT - PPTAvg} over the last minute.");
                if (Math.Abs(InitialPPT - PPTAvg) <= 3) break;
            }

            var NewBKTClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);

            ycruncherBKT.Kill(true);
            ycruncherBBP.Start();

            while (true)
            {
                var InitialPPT = GetSensorReading(PPTIndex);
                Thread.Sleep(55000);
                var PPTAvg = GetAvgSensorOverPeriod(PPTIndex, 5000);
                Console.WriteLine($"PPT changed by {InitialPPT - PPTAvg} over the last minute.");
                if (Math.Abs(InitialPPT - PPTAvg) <= 3) break;
            }

            var NewBBPClockAvg = GetAvgSensorOverPeriod(AvgEffectiveClock, 10000);

            Console.WriteLine($"New Scalar multicore average: {NewBKTClockAvg}");
            Console.WriteLine($"New AVX2|512 multicore average: {NewBBPClockAvg}");

            Console.WriteLine($"Improved average multicore clock speed in Scalar workloads by {NewBKTClockAvg / StockBKTClockAvg}");
            Console.WriteLine($"Improved average multicore clock speed in AVX2|512 workloads by {NewBBPClockAvg / StockBBPClockAvg}");

            ycruncherBBP.Kill(true);

            Console.WriteLine("Current PBO settings are temporary. Please enter your BIOS to make this configuration permanent.");
            Console.WriteLine("Final PBO settings:");

            for (int i = 0; i < SensorIndexes.Count; i++) Console.WriteLine($"Core {i}: {PBOOffsets[i]}");

            Console.WriteLine("Thank you for using Ryzen PBO VID Sync.");
            Console.WriteLine("rawhide kobayashi - https://blog.neet.works/");

            Console.WriteLine("Press ENTER to exit program...");
            Console.ReadLine();

            ycruncherBBP.Dispose();
            ycruncherBKT.Dispose();
            ryzen.Dispose();
            mmf.Dispose();

            return 0;
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

                    //Console.WriteLine(String.Format("dwSensorID : {0}", SensorElement.dwSensorID));
                    //Console.WriteLine(String.Format("dwSensorInst : {0}", SensorElement.dwSensorInst));
                    //Console.WriteLine(String.Format("szSensorNameOrig : {0}", SensorElement.szSensorNameOrig));
                    //Console.WriteLine(String.Format("szSensorNameUser : {0}", SensorElement.szSensorNameUser));
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

                    //Console.WriteLine(String.Format("tReading : {0}", ReadingElement.tReading));
                    //Console.WriteLine(String.Format("dwSensorIndex : {0} ; Sensor Name: {1}", ReadingElement.dwSensorIndex, masterSensorNames[(int)ReadingElement.dwSensorIndex]));
                    //Console.WriteLine(String.Format("dwReadingID : {0}", ReadingElement.dwSensorIndex));
                    //Console.WriteLine(String.Format("szLabelUser : {0}", ReadingElement.szLabelUser));
                    //Console.WriteLine(String.Format("szUnit : {0}", ReadingElement.szUnit));
                    //Console.WriteLine(String.Format("Value : {0}", ReadingElement.Value));

                    Match match = Regex.Match(ReadingElement.szLabelOrig, @"(?<core_vid>Core [0-9]* VID)|(?<core_mhz>Core [0-9]* T0 Effective Clock)|(?<ppt>CPU PPT)|(?<avg_eff_clock>Average Effective Clock)");

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

            for (int i = 0; i < arg.Length; i++) 
            {
                int core = Convert.ToInt32(arg[i].Split(':')[0]);
                int offset = Convert.ToInt32(arg[i].Split(':')[1]);

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
