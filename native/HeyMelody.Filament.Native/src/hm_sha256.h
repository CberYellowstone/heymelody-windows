#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

namespace hm_filament {

// Computes the digest independently from the caller's expected model digest.
std::string sha256(const std::uint8_t* data, std::size_t size);

#if defined(HM_SHA256_TEST_PORTABLE)
std::string sha256_portable_for_tests(const std::uint8_t* data, std::size_t size);
#endif

}
