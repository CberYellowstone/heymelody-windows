#include "hm_sha256.h"

#include <array>
#include <cstring>
#include <limits>
#include <stdexcept>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <bcrypt.h>
#endif

namespace hm_filament {
namespace {
using Digest = std::array<std::uint8_t, 32>;

void validate_input(const std::uint8_t* data, std::size_t size) {
    if (!data && size) throw std::invalid_argument("SHA-256 input is null");
}

std::string to_hex(const Digest& digest) {
    static constexpr char digits[] = "0123456789abcdef";
    std::string result(digest.size() * 2, '0');
    for (std::size_t i = 0; i < digest.size(); ++i) {
        result[i * 2] = digits[digest[i] >> 4];
        result[i * 2 + 1] = digits[digest[i] & 0x0f];
    }
    return result;
}

#if !defined(_WIN32) || defined(HM_SHA256_TEST_PORTABLE)
std::uint32_t rotate_right(std::uint32_t value, unsigned count) {
    return (value >> count) | (value << (32 - count));
}

void process_block(std::array<std::uint32_t, 8>& state, const std::uint8_t* block) {
    static constexpr std::uint32_t constants[64] = {
        0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
    };
    std::uint32_t words[64]{};
    for (std::size_t i = 0; i < 16; ++i) {
        const auto* p = block + i * 4;
        words[i] = std::uint32_t(p[0]) << 24 | std::uint32_t(p[1]) << 16
                | std::uint32_t(p[2]) << 8 | p[3];
    }
    for (std::size_t i = 16; i < 64; ++i) {
        const auto a = words[i - 15], b = words[i - 2];
        words[i] = words[i - 16] + (rotate_right(a, 7) ^ rotate_right(a, 18) ^ (a >> 3))
                + words[i - 7] + (rotate_right(b, 17) ^ rotate_right(b, 19) ^ (b >> 10));
    }
    std::uint32_t a = state[0], b = state[1], c = state[2], d = state[3];
    std::uint32_t e = state[4], f = state[5], g = state[6], h = state[7];
    for (std::size_t i = 0; i < 64; ++i) {
        const std::uint32_t t1 = h + (rotate_right(e, 6) ^ rotate_right(e, 11) ^ rotate_right(e, 25))
                + ((e & f) ^ (~e & g)) + constants[i] + words[i];
        const std::uint32_t t2 = (rotate_right(a, 2) ^ rotate_right(a, 13) ^ rotate_right(a, 22))
                + ((a & b) ^ (a & c) ^ (b & c));
        h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
    }
    state[0] += a; state[1] += b; state[2] += c; state[3] += d;
    state[4] += e; state[5] += f; state[6] += g; state[7] += h;
}

Digest portable_digest(const std::uint8_t* data, std::size_t size) {
    std::array<std::uint32_t, 8> state = {
        0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,
        0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19
    };
    const std::size_t complete = size - size % 64;
    for (std::size_t offset = 0; offset < complete; offset += 64) {
        process_block(state, data + offset);
    }

    // Only the final one or two blocks need padding; never duplicate the complete GLB.
    std::array<std::uint8_t, 128> tail{};
    const std::size_t remainder = size - complete;
    if (remainder) std::memcpy(tail.data(), data + complete, remainder);
    tail[remainder] = 0x80;
    const std::size_t padded = remainder < 56 ? 64 : 128;
    const std::uint64_t bits = static_cast<std::uint64_t>(size) * 8;
    for (unsigned i = 0; i < 8; ++i) tail[padded - 1 - i] = std::uint8_t(bits >> (i * 8));
    process_block(state, tail.data());
    if (padded == 128) process_block(state, tail.data() + 64);

    Digest digest{};
    for (std::size_t i = 0; i < state.size(); ++i) {
        for (unsigned j = 0; j < 4; ++j) {
            digest[i * 4 + j] = std::uint8_t(state[i] >> ((3 - j) * 8));
        }
    }
    return digest;
}
#endif
}

std::string sha256(const std::uint8_t* data, std::size_t size) {
    validate_input(data, size);
#if defined(_WIN32)
    if (size > std::numeric_limits<ULONG>::max()) {
        throw std::invalid_argument("SHA-256 input exceeds the Windows one-shot limit");
    }
    Digest digest{};
    const std::uint8_t empty = 0;
    auto* input = const_cast<std::uint8_t*>(size ? data : &empty);
    const NTSTATUS status = BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, input,
            static_cast<ULONG>(size), digest.data(), static_cast<ULONG>(digest.size()));
    if (status != 0) {
        throw std::runtime_error("Windows SHA-256 computation failed: " + std::to_string(status));
    }
    return to_hex(digest);
#else
    return to_hex(portable_digest(data, size));
#endif
}

#if defined(HM_SHA256_TEST_PORTABLE)
std::string sha256_portable_for_tests(const std::uint8_t* data, std::size_t size) {
    validate_input(data, size);
    return to_hex(portable_digest(data, size));
}
#endif
}
