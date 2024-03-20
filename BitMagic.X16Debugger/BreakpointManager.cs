﻿using BitMagic.Common;
using BitMagic.Common.Address;
using BitMagic.Decompiler;
using BitMagic.X16Debugger.DebugableFiles;
using BitMagic.X16Emulator;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace BitMagic.X16Debugger;

internal class BreakpointManager
{
    private readonly Dictionary<int, List<MemoryBreakpointMap>> _memoryBreakpoints = new();

    private readonly Dictionary<int, (Breakpoint Breakpoint, SourceBreakpoint SourceBreakpoint)> _breakpoints = new();
    private readonly Dictionary<int, int> _breakpointHitCount = new();
    private readonly Emulator _emulator;
    private readonly IdManager _idManager;
    private readonly DisassemblerManager _disassemblerManager;
    private readonly DebugableFileManager _debugableFileManager;

    private readonly HashSet<int> _debuggerBreakpoints = new(); // breakpoints which the debugger rely on.

    internal BreakpointManager(Emulator emulator, 
        IdManager idManager, DisassemblerManager disassemblerManager,
        DebugableFileManager debugableFileManager)
    {
        _emulator = emulator;
        _idManager = idManager;
        _disassemblerManager = disassemblerManager;
        _debugableFileManager = debugableFileManager;
    }

    public HashSet<int> DebuggerBreakpoints => _debuggerBreakpoints;

    public void SetDebuggerBreakpoints()
    {
        foreach (var debuggerAddress in _debuggerBreakpoints)
        {
            var (address, bank) = AddressFunctions.GetAddressBank(debuggerAddress);

            var currentBank = address >= 0xc000 ? _emulator.Memory[0x01] : _emulator.Memory[0x00];
            var (primaryAddress, secondAddress) = AddressFunctions.GetMemoryLocations(bank, address);

            // only set local breakpoint if we're in the right bank
            if (primaryAddress < 0xa000 || bank == currentBank)
                _emulator.Breakpoints[address] = 0x80;

            if (secondAddress != 0)
                _emulator.Breakpoints[secondAddress] = 0x80;
        }
    }

    private Breakpoint ConvertBreakpoint(SourceBreakpoint breakpoint, Source source, bool verified)
    {
        return new Breakpoint()
        {
            Line = breakpoint.Line,
            Id = _idManager.GetId(),
            Source = source,
            Verified = verified
        };
    }

    public List<Breakpoint> ClearBreakpoints(int debuggerAddress, int length)
    {
        var toReturn = new List<Breakpoint>();
        for (var i = debuggerAddress; i < debuggerAddress + length; i++)
        {
            if (_breakpoints.ContainsKey(i))
            {
                var bp = _breakpoints[i].Breakpoint;
                bp.Verified = false;
                toReturn.Add(bp);
                _breakpoints.Remove(i);
            }

            if (_breakpointHitCount.ContainsKey(i))
                _breakpointHitCount.Remove(i);

            var (address, ramBank, romBank) = AddressFunctions.GetMachineAddress(i);
            var (offset, secondOffset) = AddressFunctions.GetMemoryLocations(ramBank > 0 ? ramBank : romBank, address);

            var breakpointValue = _debuggerBreakpoints.Contains(i) ? (byte)0x80 : (byte)0;

            _emulator.Breakpoints[offset] = breakpointValue;
            if (secondOffset != 0)
                _emulator.Breakpoints[secondOffset] = breakpointValue;

            if (_breakpoints.ContainsKey(i))
                _breakpoints.Remove(i);
        }

        return toReturn;
    }

    public void ClearBreakpoints(DebugWrapper wrapper)
    {
        foreach (var bp in wrapper.Breakpoints)
        {
            if (bp.PrimaryAddress != 0)
                _emulator.Breakpoints[bp.PrimaryAddress] &= 0x80;
            if (bp.SecondaryAddress != 0)
                _emulator.Breakpoints[bp.SecondaryAddress] &= 0x80;

            var thisAddress = bp.SecondaryAddress == 0 ? bp.PrimaryAddress : bp.SecondaryAddress;
            if (_breakpoints.ContainsKey(thisAddress))
                _breakpoints.Remove(thisAddress);
        }
    }

    // Called when a file is loaded, this is used by the wrapper to construct its Breakpoint list.
    public List<Breakpoint> CreateBitMagicBreakpoints(int debuggerAddress, DebugWrapper wrapper, DebugableFileManager fileManager)
    {
        var toReturn = new List<Breakpoint>();
        // Breakpoints are placed in code, but we need to find where those places map to.
        // SourceBreakpoint are placed in code from VSCode
        // Breakpoint is the response, which we return as it will need to be sent to VSCode

        // Go through the file and hunt for breakpoints in the parents wrapper.
        // Can't use the actual parent, as these are not wrapped.       
        for (var i = 0; i < wrapper.ParentMap.Count; i++)
        {
            if (wrapper.ParentMap[i].relativeId != -1)
            {
                foreach (var b in wrapper.FindParentBreakpoints(i, fileManager))
                {
                    toReturn.Add(b.Breakpoint);
                    b.Breakpoint.Verified = true;

                    // set breakpoint in memory
                    var breakpointValue = _debuggerBreakpoints.Contains(debuggerAddress + i) ? (byte)0x81 : (byte)0x01;

                    var (_, bank) = AddressFunctions.GetAddressBank(debuggerAddress + i);

                    var (address, secondAddress) = AddressFunctions.GetMemoryLocations(debuggerAddress + i);
                    var currentBank = address >= 0xc000 ? _emulator.RomBankAct : _emulator.RamBankAct;

                    b.PrimaryAddress = address;
                    b.SecondaryAddress = secondAddress;

                    if (address < 0xa000 || bank == currentBank)
                        _emulator.Breakpoints[address] = breakpointValue;

                    if (secondAddress != 0)
                        _emulator.Breakpoints[secondAddress] = breakpointValue;

                    var thisAddress = secondAddress == 0 ? address : secondAddress;
                    if (!_breakpoints.ContainsKey(thisAddress))
                        _breakpoints.Add(thisAddress, (b.Breakpoint, b.SourceBreakpoint));
                }
            }
        }
        return toReturn;
    }

    // Called when VSCode sets a breakpoint
    public List<Breakpoint> HandleSetBreakpointsBitmagic(SetBreakpointsArguments arguments, DebugWrapper wrapper)
    {
        ClearBreakpoints(wrapper);
        wrapper.Breakpoints.Clear();

        var toReturn = new List<Breakpoint>();

        foreach (var sbp in arguments.Breakpoints)
        {
            var added = false;
            foreach (var (debuggerAddress, loaded) in wrapper.FindUltimateAddresses(sbp.Line - 1, _debugableFileManager))
            {
                var breakpoint = sbp.ConvertBreakpoint(arguments.Source, loaded, _idManager);

                // set system bit
                var breakpointValue = _debuggerBreakpoints.Contains(debuggerAddress) ? (byte)0x81 : (byte)0x01;

                var (_, bank) = AddressFunctions.GetAddressBank(debuggerAddress);

                var (address, secondAddress) = AddressFunctions.GetMemoryLocations(debuggerAddress);

                if (loaded)
                {
                    var currentBank = address >= 0xc000 ? _emulator.RomBankAct : _emulator.RamBankAct;

                    if (address < 0xa000 || bank == currentBank)
                        _emulator.Breakpoints[address] = breakpointValue;

                    if (secondAddress != 0)
                        _emulator.Breakpoints[secondAddress] = breakpointValue;

                    var thisAddress = secondAddress == 0 ? address : secondAddress;
                    if (!_breakpoints.ContainsKey(thisAddress))
                        _breakpoints.Add(thisAddress, (breakpoint, sbp));
                }

                added = true;
                wrapper.Breakpoints.Add(new BreakpointPair(breakpoint, sbp, address, secondAddress));
                toReturn.Add(breakpoint);
            }

            if (!added)
            {
                var breakpoint = sbp.ConvertBreakpoint(arguments.Source, false, _idManager);
                wrapper.Breakpoints.Add(new BreakpointPair(breakpoint, sbp, 0, 0));
                toReturn.Add(breakpoint);
            }
        }

        return toReturn;
    }

    public SetBreakpointsResponse HandleSetBreakpointsRequest(SetBreakpointsArguments arguments)
    {
        /// NEW CODE

        var f = _debugableFileManager.GetFileSource(arguments.Source);

        if (f != null) // can have files with breakpoints that are not part of the project
        {
            var bps = HandleSetBreakpointsBitmagic(arguments, f);

            return new SetBreakpointsResponse(bps) { Breakpoints = bps };
        }

        /// END

        // -----------------------------------------------------------------------------------------------------------
        // this isn't a BitMagic breakpoint, so set on the decompiled memory source.

        var toReturn = new SetBreakpointsResponse();

        var sourceId = arguments.Source.SourceReference ?? 0;
        var decompiledFile = _idManager.GetObject<DecompileReturn>(sourceId);

        if (decompiledFile != null && decompiledFile.Path != arguments.Source.Path)
            decompiledFile = null;

        // if the id doesn't match, then check the dissasembly cache
        if (decompiledFile == null && _disassemblerManager.DecompiledData.ContainsKey(arguments.Source.Path))
        {
            decompiledFile = _disassemblerManager.DecompiledData[arguments.Source.Path];
            sourceId = decompiledFile.ReferenceId ?? 0;
        }

        if (_memoryBreakpoints.ContainsKey(sourceId))
        {
            foreach (var breakpoint in _memoryBreakpoints[sourceId])
            {
                // Need to ensure system breakpoints are set
                var debuggerAddress = AddressFunctions.GetDebuggerAddress(breakpoint.Address, breakpoint.RamBank, breakpoint.RomBank);
                var breakpointValue = _debuggerBreakpoints.Contains(debuggerAddress) ? (byte)0x80 : (byte)0;

                var (offset, secondOffset) = AddressFunctions.GetMemoryLocations(breakpoint.RamBank > 0 ? breakpoint.RamBank : breakpoint.RomBank, breakpoint.Address);

                _emulator.Breakpoints[offset] = breakpointValue;
                if (secondOffset != 0)
                    _emulator.Breakpoints[secondOffset] = breakpointValue;

                var thisAddress = secondOffset == 0 ? offset : secondOffset;
                if (_breakpoints.ContainsKey(thisAddress))
                    _breakpoints.Remove(thisAddress);
            }

            _memoryBreakpoints[sourceId].Clear();
        }
        else
        {
            _memoryBreakpoints.Add(sourceId, new List<MemoryBreakpointMap>());
        }

        if (decompiledFile != null)
        {
            foreach (var sourceBreakpoint in arguments.Breakpoints)
            {
                if (!decompiledFile.Items.ContainsKey(sourceBreakpoint.Line))
                    continue;

                var thisLine = decompiledFile.Items[sourceBreakpoint.Line];

                var breakpoint = new Breakpoint();
                breakpoint.Source = decompiledFile.AsSource();
                breakpoint.Line = sourceBreakpoint.Line;
                breakpoint.Verified = true;
                breakpoint.Id = _idManager.GetId();

                var debuggerAddress = AddressFunctions.GetDebuggerAddress(thisLine.Address, decompiledFile.RamBank, decompiledFile.RomBank);
                var breakpointValue = _debuggerBreakpoints.Contains(debuggerAddress) ? (byte)0x81 : (byte)1;

                var toAdd = new MemoryBreakpointMap(thisLine.Address, decompiledFile.RamBank, decompiledFile.RomBank, breakpoint);
                _memoryBreakpoints[sourceId].Add(toAdd);

                var bank = thisLine.Address >= 0xc000 ? decompiledFile.RomBank : decompiledFile.RamBank;
                var currentBank = thisLine.Address >= 0xc000 ? _emulator.Memory[0x01] : _emulator.Memory[0x00];
                var (address, secondAddress) = AddressFunctions.GetMemoryLocations(bank, thisLine.Address);

                // only set local breakpoint if we're in the right bank
                if (address < 0xa000 || bank == currentBank)
                    _emulator.Breakpoints[address] = breakpointValue;

                if (secondAddress != 0)
                    _emulator.Breakpoints[secondAddress] = breakpointValue;

                var thisAddress = secondAddress == 0 ? address : secondAddress;
                if (!_breakpoints.ContainsKey(thisAddress))
                    _breakpoints.Add(thisAddress, (breakpoint, sourceBreakpoint));
            }
        }

        toReturn.Breakpoints.AddRange(_memoryBreakpoints[sourceId].Select(i => i.Breakpoint));


        return toReturn;
    }

    /// <summary>
    /// Gets a breakpoint and the times its been hit this run. Important: Increments the hitcount.
    /// </summary>
    /// <param name="address"></param>
    /// <param name="ramBank"></param>
    /// <param name="romBank"></param>
    /// <returns></returns>
    public (SourceBreakpoint? BreakPoint, int HitCount, int BreakpointValue) GetCurrentBreakpoint(int address, int ramBank, int romBank)
    {
        var (_, secondAddress) = AddressFunctions.GetMemoryLocations(address >= 0xc000 ? romBank : ramBank, address);

        var thisAddress = secondAddress == 0 ? address : secondAddress;

        int hitCount;
        if (_breakpointHitCount.ContainsKey(thisAddress))
        {
            hitCount = _breakpointHitCount[thisAddress];
            hitCount++;
            _breakpointHitCount[thisAddress] = hitCount;
        }
        else
        {
            hitCount = 1;
            _breakpointHitCount.Add(thisAddress, hitCount);
        }

        if (_breakpoints.ContainsKey(thisAddress))
            return (_breakpoints[thisAddress].SourceBreakpoint, hitCount, _emulator.Breakpoints[address]);

        return (null, hitCount, _emulator.Breakpoints[address]);
    }

    public void Clear()
    {
        //_bitMagicBreakpoints.Clear();
        _memoryBreakpoints.Clear();
        _breakpointHitCount.Clear();

        // yes this is awful.
        for (var i = 0; i < _emulator.Breakpoints.Length; i++)
        {
            _emulator.Breakpoints[i] = 0;
        }
    }
}

internal class BitMagicBreakpointMap
{
    public Breakpoint Breakpoint { get; }
    public IOutputData? Source { get; set; }

    internal BitMagicBreakpointMap(Breakpoint breakpoint, IOutputData? source)
    {
        Breakpoint = breakpoint;
        Source = source;
    }
}

internal class MemoryBreakpointMap
{
    public int Address { get; }
    public int RamBank { get; }
    public int RomBank { get; }
    public Breakpoint Breakpoint { get; }

    public MemoryBreakpointMap(int address, int ramBank, int romBank, Breakpoint breakpoint)
    {
        Address = address;
        RamBank = ramBank;
        RomBank = romBank;
        Breakpoint = breakpoint;
    }
}

internal static class SouceBreakpointExtensions
{
    internal static Breakpoint ConvertBreakpoint(this SourceBreakpoint breakpoint, Source source, bool verified, IdManager idManager)
    {
        return new Breakpoint()
        {
            Line = breakpoint.Line,
            Id = idManager.GetId(),
            Source = source,
            Verified = verified
        };
    }
}