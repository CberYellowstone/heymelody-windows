#include "hm_sha256.h"

#include <array>
#include <fstream>
#include <iostream>
#include <iterator>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
void check(const std::string& label, const std::vector<std::uint8_t>& input,
        const std::string& expected) {
    const auto actual = hm_filament::sha256(input.data(), input.size());
    if (actual != expected) throw std::runtime_error(label + ": platform SHA-256 mismatch");
#if defined(HM_SHA256_TEST_PORTABLE)
    const auto portable = hm_filament::sha256_portable_for_tests(input.data(), input.size());
    if (portable != expected) throw std::runtime_error(label + ": portable SHA-256 mismatch");
#endif
}

std::vector<std::uint8_t> read_file(const char* path) {
    std::ifstream input(path, std::ios::binary);
    if (!input) throw std::runtime_error(std::string("Cannot open SHA-256 fixture: ") + path);
    return std::vector<std::uint8_t>(
            std::istreambuf_iterator<char>(input), std::istreambuf_iterator<char>());
}
}

int main(int argc, char** argv) {
    try {
        if (argc != 3) {
            throw std::runtime_error("Usage: hm_filament_sha256_tests <Free4.glb> <Dynaudio.glb>");
        }
        check("empty", {}, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        check("abc", {'a', 'b', 'c'},
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        struct Boundary {
            std::size_t length;
            const char* expected;
        };
        // Fixed reference digests for ASCII 'a', independently calculated with .NET SHA256.
        const std::array<Boundary, 5> boundaries = {{
            {55, "9f4390f8d30c2dd92ec9f095b65e2b9ae9b0a925a5258e241c9f1e910f734318"},
            {56, "b35439a4ac6f0948b6d6f9e3c6af0f5f590ce20f1bde7090ef7970686ec6738a"},
            {63, "7d3e74a05d7db15bce4ad9ec0658ea98e3f06eeecf16b4c6fff2da457ddc2f34"},
            {64, "ffe054fe7ae0cb6dc65c3af9b61d5209f439851db43d0ba5997337df154668eb"},
            {65, "635361c48bb9eab14198e76ea8ab7f1a41685d6ad62aa9146d301d4f17eb0ae0"}
        }};
        for (const auto& boundary : boundaries) {
            check("a/" + std::to_string(boundary.length),
                    std::vector<std::uint8_t>(boundary.length, 'a'), boundary.expected);
        }
        check("Enco Free4", read_file(argv[1]),
                "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc");
        check("Enco Free4 Dynaudio", read_file(argv[2]),
                "7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6");
        std::cout << "SHA-256: 9 reference vectors passed for platform and portable implementations\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
