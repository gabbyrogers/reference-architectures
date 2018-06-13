namespace taxi
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.EventHubs;
    using Newtonsoft.Json;

    class Program
    {
        private static async Task ReadData<T>(IList<string> pathList, Func<string, T> factory,
            EventHubClient client, int randomSeed, AsyncConsole console, CancellationToken cancellationToken)
        {

            if(pathList == null)
            {
                throw new ArgumentException($"files does not exist");
            }
        
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            if (console == null)
            {
                throw new ArgumentNullException(nameof(console));
            }

            foreach (var path in pathList)
            {
                string typeName = typeof(T).Name;
                Random random = new Random(randomSeed);
                ZipArchive archive = new ZipArchive(
                    File.OpenRead(path),
                    ZipArchiveMode.Read);
                //Console.WriteLine(archive.Entries.Count);
                foreach (var entry in archive.Entries)
                {
                    using (var reader = new StreamReader(entry.Open()))
                    {
                        int lines = 0;
                        var batches = reader.ReadLines()
                            .Skip(1)
                            .Select(s => {
                                lines++;
                                return new EventData(Encoding.UTF8.GetBytes(
                                    JsonConvert.SerializeObject(factory(s))));
                            })
                            .Partition();
                        int i = 0;
                        foreach (var batch in batches)
                        {
                            // Wait for a random interval to introduce some delays.
                            await Task.Delay(random.Next(100, 1000))
                                .ConfigureAwait(false);
                            await client.SendAsync(batch)
                                .ConfigureAwait(false);
                            if (++i % 10 == 0)
                            {
                                await console.WriteLine($"{typeName} lines consumed: {lines}")
                                    .ConfigureAwait(false);
                                await console.WriteLine($"Created {i} {typeName} batches")
                                    .ConfigureAwait(false);
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }
                        }

                        await console.WriteLine($"Created {i} total {typeName} batches")
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        private static (string RideConnectionString,
                        string FareConnectionString,
                        string RideDataFilePath,
                        string FareDataFilePath,
                        int MillisecondsToRun) ParseArguments()
        {

            var rideConnectionString = Environment.GetEnvironmentVariable("RIDE_EVENT_HUB");
            var fareConnectionString = Environment.GetEnvironmentVariable("FARE_EVENT_HUB");
            var rideDataFilePath = Environment.GetEnvironmentVariable("RIDE_DATA_FILE_PATH");
            var fareDataFilePath = Environment.GetEnvironmentVariable("FARE_DATA_FILE_PATH"); 
            var numberOfMillisecondsToRun = (int.TryParse(Environment.GetEnvironmentVariable("SECONDS_TO_RUN"), out int temp) ? temp : 0) * 1000;

            if (string.IsNullOrWhiteSpace(rideConnectionString))
            {
                throw new ArgumentException("rideConnectionString must be provided");
            }

            if (string.IsNullOrWhiteSpace(fareConnectionString))
            {
                throw new ArgumentException("rideConnectionString must be provided");
            }

            if (string.IsNullOrWhiteSpace(rideDataFilePath))
            {
                throw new ArgumentException("rideDataFilePath must be provided");
            }

            if (Directory.GetFiles(rideDataFilePath).Length==0)
            {
                throw new ArgumentException($"Ride data files at {rideDataFilePath} does not exist");
            }

            if (string.IsNullOrWhiteSpace(fareDataFilePath))
            {
                throw new ArgumentException("fareDataFilePath must be provided");
            }

            if (Directory.GetFiles(fareDataFilePath).Length==0)
            {
                throw new ArgumentException($"Fare data files at  {fareDataFilePath} does not exist");
            }

            return (rideConnectionString, fareConnectionString, rideDataFilePath, fareDataFilePath, numberOfMillisecondsToRun);
        }

        private class AsyncConsole
        {
            private BlockingCollection<string> _blockingCollection = new BlockingCollection<string>();
            private CancellationToken _cancellationToken;
            private Task _writerTask;

            public AsyncConsole(CancellationToken cancellationToken = default(CancellationToken))
            {
                _cancellationToken = cancellationToken;
                _writerTask = Task.Factory.StartNew((state) => {
                    var token = (CancellationToken)state;
                    string msg;
                    while (!token.IsCancellationRequested)
                    {
                        if (_blockingCollection.TryTake(out msg, 500))
                        {
                            Console.WriteLine(msg);
                        }
                    }

                    while (_blockingCollection.TryTake(out msg, 100))
                    {
                        Console.WriteLine(msg);
                    }
                }, _cancellationToken, TaskCreationOptions.LongRunning);
            }

            public Task WriteLine(string toWrite)
            {
                _blockingCollection.Add(toWrite);
                return Task.FromResult(0);
            }

            public Task WriterTask
            {
                get { return _writerTask; }
            }
        }
        public static async Task<int> Main(string[] args)
        {
            try
            {
                var arguments = ParseArguments();
                var rideClient = EventHubClient.CreateFromConnectionString(
                    arguments.RideConnectionString
                );
                var fareClient = EventHubClient.CreateFromConnectionString(
                    arguments.FareConnectionString
                );

                CancellationTokenSource cts = arguments.MillisecondsToRun == 0 ? new CancellationTokenSource() :
                    new CancellationTokenSource(arguments.MillisecondsToRun);
                Console.CancelKeyPress += (s, e) => {
                    //Console.WriteLine("Cancelling data generation");
                    cts.Cancel();
                    e.Cancel = true;
                };
           
                var rideDataFiles = from file in Directory.EnumerateFiles(arguments.RideDataFilePath)
                                    orderby   Int32.Parse(Path.GetFileName(file).Split("_")[2].ToString().Split(".")[0].ToString()) ascending
                                    select file;

                 var fareDataFiles = from file in Directory.EnumerateFiles(arguments.FareDataFilePath)
                                    orderby   Int32.Parse(Path.GetFileName(file).Split("_")[2].ToString().Split(".")[0].ToString()) ascending
                                    select file ;

                AsyncConsole console = new AsyncConsole(cts.Token);
               
                var rideTask = ReadData<TaxiRide>(rideDataFiles.ToArray(),   
                    TaxiRide.FromString, rideClient, 100, console, cts.Token);
                var fareTask = ReadData<TaxiFare>(fareDataFiles.ToArray(),
                    TaxiFare.FromString, fareClient, 200, console, cts.Token);
                await Task.WhenAll(rideTask, fareTask, console.WriterTask);
            
                Console.WriteLine("Data generation complete");
            }
            catch (ArgumentException ae)
            {
                Console.WriteLine(ae.Message);
                return 1;
            }

            return 0;
        }
    }
}
