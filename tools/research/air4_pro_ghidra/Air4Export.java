// Export every function discovered by Ghidra; discovery is not proof of completeness.
// @category Research.Air4Pro

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressIterator;
import ghidra.program.model.address.AddressRange;
import ghidra.program.model.address.AddressRangeIterator;
import ghidra.program.model.listing.CodeUnit;
import ghidra.program.model.listing.CodeUnitIterator;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import java.io.BufferedWriter;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.DirectoryStream;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

public class Air4Export extends GhidraScript {
    // JDK-only guard: offline tests exercise this code without Ghidra or firmware.
    static final class OutputFiles {
        static void prepareDirectory(Path output) throws IOException {
            if (Files.exists(output, LinkOption.NOFOLLOW_LINKS)) {
                if (!Files.isDirectory(output, LinkOption.NOFOLLOW_LINKS)) {
                    throw new FileAlreadyExistsException(output.toString(), null,
                        "Output must be a new directory or an existing empty directory, not a link or file");
                }
                try (DirectoryStream<Path> entries = Files.newDirectoryStream(output)) {
                    if (entries.iterator().hasNext()) {
                        throw new FileAlreadyExistsException(output.toString(), null,
                            "Refusing to reuse a nonempty evidence directory");
                    }
                }
            } else {
                Path parent = output.toAbsolutePath().getParent();
                if (parent != null) Files.createDirectories(parent);
                Files.createDirectory(output);
            }
            // This exclusive child creation also prevents two exporters from claiming an empty directory.
            Files.createDirectory(output.resolve("functions"));
        }

        static BufferedWriter writer(Path path) throws IOException {
            return Files.newBufferedWriter(path, StandardCharsets.UTF_8,
                StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        }

        static void writeString(Path path, String value) throws IOException {
            Files.writeString(path, value, StandardCharsets.UTF_8,
                StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE);
        }
    }

    private final Gson gson = new GsonBuilder().disableHtmlEscaping().create();
    private long imageBase = 0x0c010000L;

    private String hex(Address address) { return "0x" + address.toString(); }
    private BufferedWriter writer(Path path) throws Exception {
        return OutputFiles.writer(path);
    }
    private void jsonLine(BufferedWriter output, Object value) throws Exception {
        output.write(gson.toJson(value)); output.newLine();
    }
    private List<String> functions(Iterable<Function> input) {
        List<String> output = new ArrayList<>();
        for (Function function : input) output.add(hex(function.getEntryPoint()));
        return output;
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length == 0) throw new IllegalArgumentException("Output directory is required");
        Path output = Path.of(args[0]);
        int timeout = args.length > 1 ? Integer.parseInt(args[1]) : 45;
        OutputFiles.prepareDirectory(output);
        DecompInterface decompiler = new DecompInterface();
        DecompileOptions options = new DecompileOptions();
        options.grabFromProgram(currentProgram);
        decompiler.setOptions(options);
        decompiler.toggleCCode(true);
        decompiler.toggleSyntaxTree(true);
        if (!decompiler.openProgram(currentProgram)) {
            throw new IllegalStateException(decompiler.getLastMessage());
        }
        long started = System.currentTimeMillis();
        int discovered = 0, completed = 0, failed = 0;
        long bodyBytes = 0;
        try (BufferedWriter index = writer(output.resolve("functions.jsonl"))) {
            FunctionIterator iterator = currentProgram.getFunctionManager().getFunctions(true);
            while (iterator.hasNext() && !monitor.isCancelled()) {
                Function function = iterator.next();
                if (function.isExternal()) continue;
                MemoryBlock block = currentProgram.getMemory().getBlock(function.getEntryPoint());
                if (block == null || !block.isExecute()) continue;
                discovered++;
                String stem = function.getEntryPoint().toString();
                Map<String,Object> row = new LinkedHashMap<>();
                row.put("entry", hex(function.getEntryPoint()));
                row.put("raw_offset", function.getEntryPoint().getOffset() - imageBase);
                row.put("name", function.getName());
                row.put("signature", function.getSignature().toString());
                row.put("body_bytes", function.getBody().getNumAddresses());
                bodyBytes += function.getBody().getNumAddresses();
                List<List<String>> ranges = new ArrayList<>();
                AddressRangeIterator rangeIterator = function.getBody().getAddressRanges();
                while (rangeIterator.hasNext()) {
                    AddressRange range = rangeIterator.next();
                    ranges.add(List.of(hex(range.getMinAddress()), hex(range.getMaxAddress())));
                }
                row.put("body_ranges", ranges);
                row.put("calls", functions(function.getCalledFunctions(monitor)));
                row.put("callers", functions(function.getCallingFunctions(monitor)));
                row.put("thunk", function.isThunk());
                DecompileResults result = decompiler.decompileFunction(function, timeout, monitor);
                String error = result.getErrorMessage();
                row.put("decompile_status", result.decompileCompleted() ? "completed" : "failed");
                row.put("decompile_error", error == null ? "" : error);
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    String c = result.getDecompiledFunction().getC();
                    OutputFiles.writeString(output.resolve("functions").resolve(stem + ".c"), c);
                    row.put("decompiled_file", "functions/" + stem + ".c");
                    completed++;
                } else {
                    failed++;
                }
                try (BufferedWriter assembly = writer(output.resolve("functions").resolve(stem + ".asm"))) {
                    CodeUnitIterator units = currentProgram.getListing().getCodeUnits(function.getBody(), true);
                    while (units.hasNext()) {
                        CodeUnit unit = units.next();
                        assembly.write(hex(unit.getAddress()) + " " + unit.toString()); assembly.newLine();
                    }
                }
                row.put("assembly_file", "functions/" + stem + ".asm");
                jsonLine(index, row);
                if (discovered % 250 == 0) {
                    index.flush();
                    println("AIR4_EXPORT checkpoint discovered=" + discovered + " completed=" + completed + " failed=" + failed);
                }
            }
        } finally { decompiler.dispose(); }
        try (BufferedWriter listing = writer(output.resolve("program-listing.txt"));
             BufferedWriter references = writer(output.resolve("references.jsonl"))) {
            CodeUnitIterator units = currentProgram.getListing().getCodeUnits(true);
            while (units.hasNext()) {
                CodeUnit unit = units.next();
                if (unit instanceof Data && !((Data) unit).isDefined()) continue;
                listing.write(hex(unit.getAddress()) + " " + unit.toString()); listing.newLine();
                for (Reference reference : unit.getReferencesFrom()) {
                    Map<String,Object> row = new LinkedHashMap<>();
                    row.put("from", hex(reference.getFromAddress()));
                    row.put("to", hex(reference.getToAddress()));
                    row.put("type", reference.getReferenceType().toString());
                    row.put("operand", reference.getOperandIndex());
                    Function owner = currentProgram.getFunctionManager().getFunctionContaining(reference.getFromAddress());
                    row.put("function", owner == null ? null : hex(owner.getEntryPoint()));
                    jsonLine(references, row);
                }
            }
        }
        int stringCount = 0;
        try (BufferedWriter strings = writer(output.resolve("strings-and-xrefs.jsonl"))) {
            DataIterator iterator = currentProgram.getListing().getDefinedData(true);
            while (iterator.hasNext()) {
                Data data = iterator.next();
                Object value = data.getValue();
                if (!(value instanceof String)) continue;
                Map<String,Object> row = new LinkedHashMap<>();
                row.put("address", hex(data.getAddress()));
                row.put("length", data.getLength());
                row.put("value", value);
                List<Map<String,Object>> xrefs = new ArrayList<>();
                ReferenceIterator references = currentProgram.getReferenceManager().getReferencesTo(data.getAddress());
                while (references.hasNext()) {
                    Reference reference = references.next();
                    Map<String,Object> ref = new LinkedHashMap<>();
                    ref.put("from", hex(reference.getFromAddress()));
                    ref.put("type", reference.getReferenceType().toString());
                    Function owner = currentProgram.getFunctionManager().getFunctionContaining(reference.getFromAddress());
                    ref.put("function", owner == null ? null : hex(owner.getEntryPoint()));
                    xrefs.add(ref);
                }
                row.put("xrefs", xrefs);
                jsonLine(strings, row); stringCount++;
            }
        }
        Map<String,Object> summary = new LinkedHashMap<>();
        summary.put("program", currentProgram.getName());
        summary.put("language", currentProgram.getLanguageID().toString());
        summary.put("compiler_spec", currentProgram.getCompilerSpec().getCompilerSpecID().toString());
        summary.put("image_base", hex(currentProgram.getImageBase()));
        summary.put("memory_bytes_including_alias", currentProgram.getMemory().getSize());
        summary.put("discovered_functions", discovered);
        summary.put("decompiled_completed", completed);
        summary.put("decompiled_failed", failed);
        summary.put("sum_discovered_function_body_bytes", bodyBytes);
        summary.put("defined_strings", stringCount);
        summary.put("duration_ms", System.currentTimeMillis() - started);
        summary.put("cancelled", monitor.isCancelled());
        summary.put("coverage_warning", "Every discovered function was attempted; undiscovered code, aliases, data and architecture assumptions remain unverified.");
        OutputFiles.writeString(output.resolve("summary.json"), new GsonBuilder().setPrettyPrinting().create().toJson(summary));
        println("AIR4_EXPORT finished " + gson.toJson(summary));
    }
}
