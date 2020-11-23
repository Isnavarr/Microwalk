using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GdbRspClientLibrary;
using GdbRspClientLibrary.Debuggers;
using GdbRspClientLibrary.Enumerations;
using GdbRspClientLibrary.Models;
using GdbRspClientLibrary.Models.StopReplies;
using GdbTracer;
using GdbTracer.Packets;
using Microwalk.Extensions;
using Microwalk.TraceEntryTypes;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TraceGeneration.Modules
{
    // TODO refactor this into an own library; use plugin-architecture for Microwalk, so the trace library can access Microwalk's trace types?
    [FrameworkModule("gdb", "Generates traces using the GDB remote client.")]
    internal class GdbTraceGenerator : TraceStage
    {
        // Not supported right now, although in theory it _might_ work, if there is a pool of debugger connections.
        // However, one has to be sure that all instances have the exact same state, to get reliable results.
        // The analysis result should not depend on which testcase gets assigned to which debugger instance.
        public override bool SupportsParallelism { get; } = false;

        /// <summary>
        /// The trace output directory.
        /// </summary>
        private DirectoryInfo _outputDirectory;

        /// <summary>
        /// TCP connection to the remote target.
        /// </summary>
        private TcpClient _debuggerTcpClient;

        /// <summary>
        /// Debugger connection.
        /// </summary>
        private RspConnection _debuggerConnection;

        /// <summary>
        /// The debugger used for tracing.
        /// </summary>
        private QemuX64Debugger _debugger;

        /// <summary>
        /// Cancellation token for all operations running in this object.
        /// </summary>
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        /// <summary>
        /// Address of the communication buffer in target memory.
        /// </summary>
        private ulong _bufferAddress;

        /// <summary>
        /// Local copy of the communication buffer.
        /// </summary>
        private readonly byte[] _localBuffer = new byte[_bufferLength];

        /// <summary>
        /// Size of the communication buffer.
        /// </summary>
        private static readonly unsafe int _bufferLength = sizeof(TestcaseRequestPacket);

        public override async Task GenerateTraceAsync(TraceEntity traceEntity)
        {
            // Create and open trace file
            await using var traceFileStream = File.Create(Path.Join(_outputDirectory.FullName, $"t{traceEntity.Id}.trace"));
            await using var traceFileWriter = new BinaryWriter(traceFileStream);

            // Resume execution until next testcase is requested
            var resumeTask = _debugger.ResumeExecutionAsync(new[] { new VContCommand { Action = VContAction.Continue } }, _cancellationTokenSource.Token);

            // Read testcase
            byte[] testcaseData = await File.ReadAllBytesAsync(traceEntity.TestcaseFilePath);

            // Wait for testcase request
            var stopReply = await resumeTask;
            if(!IsCommunicationWatchpoint(stopReply.StopReply))
                throw new Exception($"Unexpected stop: {stopReply}");
            var packetType = await RetrievePacketAsync();
            if(packetType != ApiPacketType.TestcaseRequest)
                throw new Exception($"Unexpected packet type \"{packetType}\", expected \"{ApiPacketType.TestcaseRequest}\".");

            // Derive testcase buffer address
            var testcaseRequest = MemoryMarshal.Read<TestcaseRequestPacket>(_localBuffer[12..]);

            // Write testcase data
            if(testcaseData.Length > testcaseRequest.BufferSize)
                throw new Exception($"Insufficient testcase buffer space: Target offered {testcaseRequest.BufferSize} bytes, {testcaseData.Length} bytes needed.");
            await _debugger.WriteMemoryAsync(testcaseRequest.Address, testcaseData, _cancellationTokenSource.Token);
            testcaseRequest.TestcaseSize = testcaseData.Length;
            MemoryMarshal.Write(_localBuffer, ref testcaseRequest);
            _localBuffer[0] = (byte)CommunicationTriggerCode.DebuggerWrite;
            await _debugger.WriteMemoryAsync(_bufferAddress, _localBuffer, _cancellationTokenSource.Token);

            // Wait for testcase start
            resumeTask = _debugger.ResumeExecutionAsync(new[] { new VContCommand { Action = VContAction.Continue } }, _cancellationTokenSource.Token);
            stopReply = await resumeTask;
            if(!IsCommunicationWatchpoint(stopReply.StopReply))
                throw new Exception($"Unexpected stop: {stopReply}");
            packetType = await RetrievePacketAsync();
            if(packetType != ApiPacketType.TestcaseStart)
                throw new Exception($"Unexpected packet type \"{packetType}\", expected \"{ApiPacketType.TestcaseStart}\".");

            // Complete first single step
            stopReply = await _debugger.ResumeExecutionAsync(new[] { new VContCommand { Action = VContAction.Step } }, _cancellationTokenSource.Token);

            // Single step until testcase end is reached
            bool stepover = false;
            int nextAllocationId = 0;
            var allocationLookup = new SortedList<ulong, Allocation>();
            var allocationData = new List<Allocation>();
            ulong stackPointerMin = 0xFFFF_FFFF_FFFF_FFFFUL;
            ulong stackPointerMax = 0x0000_0000_0000_0000UL;
            while(true)
            {
                // Kick off execution of next step
                // This will take a while, so performing asynchronously .....????? TODO ??
                if(stepover)
                    resumeTask = _debugger.ResumeExecutionAsync(new[] { new VContCommand { Action = VContAction.Continue } }, _cancellationTokenSource.Token);
                else
                    resumeTask = _debugger.ResumeExecutionAsync(new[] { new VContCommand { Action = VContAction.Step } }, _cancellationTokenSource.Token);

                // Communication?
                if(IsCommunicationWatchpoint(stopReply.StopReply))
                {
                    packetType = await RetrievePacketAsync();
                    switch(packetType)
                    {
                        case ApiPacketType.Allocation:
                        {
                            var packet = MemoryMarshal.Read<AllocationPacket>(_localBuffer[12..]);

                            // Create entry
                            var entry = new Allocation
                            {
                                Id = nextAllocationId++,
                                Size = (uint)packet.Length,
                                Address = packet.Address
                            };
                            entry.Store(traceFileWriter);

                            // Store allocation information
                            allocationLookup[entry.Address] = entry;
                            allocationData.Add(entry);

                            break;
                        }

                        case ApiPacketType.Free:
                        {
                            var packet = MemoryMarshal.Read<FreePacket>(_localBuffer[12..]);

                            // Find corresponding allocation
                            if(!allocationLookup.TryGetValue(packet.Address, out var allocationEntry))
                            {
                                await Logger.LogWarningAsync($"Free of address {packet.Address:X16} does not correspond to any allocation, skipping");
                                break;
                            }

                            // Create entry
                            var entry = new Free
                            {
                                Id = allocationEntry.Id
                            };
                            entry.Store(traceFileWriter);

                            // Remove entry from allocation list
                            allocationLookup.Remove(allocationEntry.Address);

                            break;
                        }

                        case ApiPacketType.TestcaseEnd:
                        {
                            //var packet = MemoryMarshal.Read<TestcaseEndPacket>(_localBuffer[12..]);
                            
                            

                            break;
                        }

                        case ApiPacketType.StackInfo:
                        {
                            var packet = MemoryMarshal.Read<StackInfoPacket>(_localBuffer[12..]);

                            // Save stack pointer data
                            stackPointerMin = packet.TopAddress;
                            stackPointerMax = packet.BaseAddress;

                            break;
                        }

                        case ApiPacketType.StepoverBegin:
                        {
                            //var packet = MemoryMarshal.Read<StepoverBeginPacket>(_localBuffer[12..]);

                            stepover = true;
                            
                            break;
                        }

                        case ApiPacketType.StepoverEnd:
                        {
                            //var packet = MemoryMarshal.Read<StepoverEndPacket>(_localBuffer[12..]);

                            stepover = false;
                            
                            break;
                        }

                        default:
                            throw new Exception($"Unexpected packet type: {packetType}.");
                    }
                }
                else
                {
                    // No communication watchpoint, so handle the next instruction
                    
                }

                // Wait for next single step to complete
                stopReply = await resumeTask;
            }
        }

        /// <summary>
        /// Downloads the current communication packet from the remote target, and returns its type.
        /// </summary>
        /// <returns></returns>
        private async Task<ApiPacketType> RetrievePacketAsync()
        {
            // Read memory
            await _debugger.ReadMemoryAsync(_bufferAddress, _bufferLength, _localBuffer, _cancellationTokenSource.Token);

            // Retrieve type
            return (ApiPacketType)BinaryPrimitives.ReadInt32LittleEndian(_localBuffer[8..]);
        }

        /// <summary>
        /// Returns whether the given stop reply packet was triggered by an access to the communication buffer.
        /// </summary>
        /// <param name="stopReply">Stop reply packet.</param>
        /// <returns></returns>
        private bool IsCommunicationWatchpoint(IStopReply stopReply)
        {
            return stopReply is StopReplySignalReceived signal
                   && signal.Watchpoints.ContainsKey(_bufferAddress);
        }

        internal override async Task InitAsync(YamlMappingNode moduleOptions)
        {
            // Extract mandatory configuration values
            _outputDirectory = new DirectoryInfo(moduleOptions.GetChildNodeWithKey("output-directory").GetNodeString());
            if(!_outputDirectory.Exists)
                _outputDirectory.Create();

            // Connect to remote
            await Logger.LogDebugAsync("Connecting to remote target");
            _debuggerTcpClient = new TcpClient();
            await _debuggerTcpClient.ConnectAsync("localhost", 1234); // TODO input
            _debuggerConnection = new RspConnection(_debuggerTcpClient.GetStream());

            // Initialize debugger
            await Logger.LogDebugAsync("Creating debugger");
            _debugger = new QemuX64Debugger(_debuggerConnection);
            await _debugger.InitAsync(_cancellationTokenSource.Token);

            // Prepare breakpoints
            await Logger.LogDebugAsync("Installing communication breakpoint");
            _bufferAddress = 0xffffffff80111020; // TODO input
            await _debugger.InsertBreakpointAsync(BreakpointType.WriteWatchpoint, _bufferAddress, 1, _cancellationTokenSource.Token);
            await _debugger.WriteMemoryAsync(_bufferAddress, new[] { (byte)CommunicationTriggerCode.DebuggerWrite }, _cancellationTokenSource.Token);
        }

        public override async Task UninitAsync()
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.Dispose();
            await _debugger.DisposeAsync();
            _debuggerConnection.Dispose();
            _debuggerTcpClient.Dispose();
        }
    }
}