// Prepare the two observed address aliases of the Air4 Pro v125 raw image.
// @category Research.Air4Pro

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.SourceType;
import java.math.BigInteger;

public class Air4Import extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        long imageSize = currentProgram.getMemory().getSize();
        long executableBase = args.length > 0 ? Long.decode(args[0]) : 0x0c010000L;
        long dataBase = args.length > 1 ? Long.decode(args[1]) : 0x2c010000L;
        Address start = toAddr(executableBase);
        Address end = toAddr(executableBase + imageSize - 1);
        Memory memory = currentProgram.getMemory();
        MemoryBlock original = memory.getBlock(start);
        if (original == null || original.getSize() != imageSize) {
            throw new IllegalStateException("Expected one raw image at executable base");
        }
        original.setName("IMAGE_EXEC_ALIAS");
        original.setRead(true);
        original.setWrite(false);
        original.setExecute(true);
        currentProgram.getProgramContext().setValue(
            currentProgram.getRegister("TMode"), start, end, BigInteger.ONE);
        MemoryBlock alias = memory.getBlock(toAddr(dataBase));
        if (alias == null) {
            alias = memory.createByteMappedBlock(
                "IMAGE_DATA_ALIAS", toAddr(dataBase), start, imageSize, false);
        }
        alias.setRead(true);
        alias.setWrite(true);
        alias.setExecute(false);
        // The input image contains an LDR/BX boot stub at raw offset 0x10 and
        // its odd Thumb destination in the literal at raw offset 0x54.
        long destination = Integer.toUnsignedLong(memory.getInt(toAddr(executableBase + 0x54)));
        if ((destination & 1) == 0 || destination < executableBase || destination >= executableBase + imageSize) {
            throw new IllegalStateException("Boot destination does not match observed Thumb alias");
        }
        for (long entry : new long[] { executableBase + 0x10, destination & ~1L }) {
            Address address = toAddr(entry);
            currentProgram.getSymbolTable().addExternalEntryPoint(address);
            disassemble(address);
            Function function = getFunctionAt(address);
            if (function == null) {
                function = createFunction(address, entry == executableBase + 0x10 ? "ImageBootStub" : "ImageBootEntry");
            }
            if (function != null) {
                function.setName(entry == executableBase + 0x10 ? "ImageBootStub" : "ImageBootEntry", SourceType.USER_DEFINED);
            }
        }
        // Defining only the already observed strings creates anchors without
        // guessing that arbitrary image bytes are instructions or functions.
        long[] anchors = {0x130a52L, 0x130cc4L, 0x130f83L, 0x134dfaL};
        for (long offset : anchors) {
            for (long base : new long[] {executableBase, dataBase}) {
                Address address = toAddr(base + offset);
                if (getDataAt(address) == null && getInstructionAt(address) == null) {
                    try { createAsciiString(address); }
                    catch (Exception error) { println("String anchor failed at " + address + ": " + error.getMessage()); }
                }
            }
        }
        println("AIR4_IMPORT image_bytes=" + imageSize + " boot=" + Long.toHexString(destination) +
            " executable_alias=" + Long.toHexString(executableBase) + " data_alias=" + Long.toHexString(dataBase));
    }
}
