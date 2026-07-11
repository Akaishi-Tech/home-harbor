#include "avb.h"
#include "console.h"
#include "globals.h"
#include "partitions.h"
#include "util.h"
#include "variables.h"

#ifndef HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE
#define HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE 0U
static const uint8_t HomeHarborTrustedAvbPublicKey[1] = {0};
#endif

typedef struct {
    UINT32 state[8];
    UINT64 bit_count;
    UINT8 buffer[64];
    UINTN buffer_size;
} SHA256_CONTEXT;

static UINT32 rotr32(UINT32 value, UINT32 count) {
    return (value >> count) | (value << (32U - count));
}

static void sha256_transform(SHA256_CONTEXT *ctx, const UINT8 block[64]) {
    static const UINT32 k[64] = {
        0x428a2f98U, 0x71374491U, 0xb5c0fbcfU, 0xe9b5dba5U,
        0x3956c25bU, 0x59f111f1U, 0x923f82a4U, 0xab1c5ed5U,
        0xd807aa98U, 0x12835b01U, 0x243185beU, 0x550c7dc3U,
        0x72be5d74U, 0x80deb1feU, 0x9bdc06a7U, 0xc19bf174U,
        0xe49b69c1U, 0xefbe4786U, 0x0fc19dc6U, 0x240ca1ccU,
        0x2de92c6fU, 0x4a7484aaU, 0x5cb0a9dcU, 0x76f988daU,
        0x983e5152U, 0xa831c66dU, 0xb00327c8U, 0xbf597fc7U,
        0xc6e00bf3U, 0xd5a79147U, 0x06ca6351U, 0x14292967U,
        0x27b70a85U, 0x2e1b2138U, 0x4d2c6dfcU, 0x53380d13U,
        0x650a7354U, 0x766a0abbU, 0x81c2c92eU, 0x92722c85U,
        0xa2bfe8a1U, 0xa81a664bU, 0xc24b8b70U, 0xc76c51a3U,
        0xd192e819U, 0xd6990624U, 0xf40e3585U, 0x106aa070U,
        0x19a4c116U, 0x1e376c08U, 0x2748774cU, 0x34b0bcb5U,
        0x391c0cb3U, 0x4ed8aa4aU, 0x5b9cca4fU, 0x682e6ff3U,
        0x748f82eeU, 0x78a5636fU, 0x84c87814U, 0x8cc70208U,
        0x90befffaU, 0xa4506cebU, 0xbef9a3f7U, 0xc67178f2U
    };
    UINT32 w[64];
    UINT32 a;
    UINT32 b;
    UINT32 c;
    UINT32 d;
    UINT32 e;
    UINT32 f;
    UINT32 g;
    UINT32 h;
    UINTN i;

    for (i = 0; i < 16; i++) {
        w[i] = read_be32(block, i * 4);
    }
    for (i = 16; i < 64; i++) {
        UINT32 s0 = rotr32(w[i - 15], 7) ^ rotr32(w[i - 15], 18) ^ (w[i - 15] >> 3);
        UINT32 s1 = rotr32(w[i - 2], 17) ^ rotr32(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }

    a = ctx->state[0];
    b = ctx->state[1];
    c = ctx->state[2];
    d = ctx->state[3];
    e = ctx->state[4];
    f = ctx->state[5];
    g = ctx->state[6];
    h = ctx->state[7];

    for (i = 0; i < 64; i++) {
        UINT32 s1 = rotr32(e, 6) ^ rotr32(e, 11) ^ rotr32(e, 25);
        UINT32 ch = (e & f) ^ ((~e) & g);
        UINT32 temp1 = h + s1 + ch + k[i] + w[i];
        UINT32 s0 = rotr32(a, 2) ^ rotr32(a, 13) ^ rotr32(a, 22);
        UINT32 maj = (a & b) ^ (a & c) ^ (b & c);
        UINT32 temp2 = s0 + maj;
        h = g;
        g = f;
        f = e;
        e = d + temp1;
        d = c;
        c = b;
        b = a;
        a = temp1 + temp2;
    }

    ctx->state[0] += a;
    ctx->state[1] += b;
    ctx->state[2] += c;
    ctx->state[3] += d;
    ctx->state[4] += e;
    ctx->state[5] += f;
    ctx->state[6] += g;
    ctx->state[7] += h;
}

static void sha256_init(SHA256_CONTEXT *ctx) {
    ctx->state[0] = 0x6a09e667U;
    ctx->state[1] = 0xbb67ae85U;
    ctx->state[2] = 0x3c6ef372U;
    ctx->state[3] = 0xa54ff53aU;
    ctx->state[4] = 0x510e527fU;
    ctx->state[5] = 0x9b05688cU;
    ctx->state[6] = 0x1f83d9abU;
    ctx->state[7] = 0x5be0cd19U;
    ctx->bit_count = 0;
    ctx->buffer_size = 0;
}

static void sha256_update(SHA256_CONTEXT *ctx, const UINT8 *data, UINTN length) {
    UINTN offset = 0;
    ctx->bit_count += ((UINT64)length) * 8ULL;

    while (offset < length) {
        UINTN space = 64U - ctx->buffer_size;
        UINTN chunk = (length - offset) < space ? (length - offset) : space;
        copy_bytes(ctx->buffer + ctx->buffer_size, data + offset, chunk);
        ctx->buffer_size += chunk;
        offset += chunk;
        if (ctx->buffer_size == 64U) {
            sha256_transform(ctx, ctx->buffer);
            ctx->buffer_size = 0;
        }
    }
}

static void sha256_final(SHA256_CONTEXT *ctx, UINT8 digest[SHA256_DIGEST_BYTES]) {
    UINTN i;
    UINT64 bit_count = ctx->bit_count;

    ctx->buffer[ctx->buffer_size++] = 0x80;
    if (ctx->buffer_size > 56U) {
        while (ctx->buffer_size < 64U) {
            ctx->buffer[ctx->buffer_size++] = 0;
        }
        sha256_transform(ctx, ctx->buffer);
        ctx->buffer_size = 0;
    }
    while (ctx->buffer_size < 56U) {
        ctx->buffer[ctx->buffer_size++] = 0;
    }
    for (i = 0; i < 8; i++) {
        ctx->buffer[56 + i] = (UINT8)(bit_count >> (56 - (i * 8)));
    }
    sha256_transform(ctx, ctx->buffer);

    for (i = 0; i < 8; i++) {
        digest[i * 4] = (UINT8)(ctx->state[i] >> 24);
        digest[i * 4 + 1] = (UINT8)(ctx->state[i] >> 16);
        digest[i * 4 + 2] = (UINT8)(ctx->state[i] >> 8);
        digest[i * 4 + 3] = (UINT8)ctx->state[i];
    }
}

static void hex_encode(const UINT8 *bytes, UINTN length, char *output) {
    static const char alphabet[] = "0123456789abcdef";
    UINTN i;

    for (i = 0; i < length; i++) {
        output[i * 2U] = alphabet[bytes[i] >> 4];
        output[i * 2U + 1U] = alphabet[bytes[i] & 0x0fU];
    }
    output[length * 2U] = 0;
}

static int vbmeta_image_size(const UINT8 *image, UINTN image_size, UINTN *verified_size) {
    UINT64 auth_size;
    UINT64 aux_size;

    if (!image || !verified_size ||
        image_size < AVB_VBMETA_HEADER_SIZE ||
        image[0] != 'A' || image[1] != 'V' || image[2] != 'B' || image[3] != '0') {
        return 0;
    }

    auth_size = read_be64(image, 12);
    aux_size = read_be64(image, 20);
    if (auth_size > image_size ||
        aux_size > image_size ||
        AVB_VBMETA_HEADER_SIZE > image_size - (UINTN)auth_size ||
        AVB_VBMETA_HEADER_SIZE + (UINTN)auth_size > image_size - (UINTN)aux_size) {
        return 0;
    }

    *verified_size = AVB_VBMETA_HEADER_SIZE + (UINTN)auth_size + (UINTN)aux_size;
    return 1;
}

static int calculate_vbmeta_digest(
    const UINT8 *image,
    UINTN image_size,
    char output[VBMETA_DIGEST_BUFFER_SIZE]) {
    UINTN verified_size;
    UINT8 digest[SHA256_DIGEST_BYTES];
    SHA256_CONTEXT sha;

    output[0] = 0;
    if (!vbmeta_image_size(image, image_size, &verified_size)) {
        return 0;
    }

    sha256_init(&sha);
    sha256_update(&sha, image, verified_size);
    sha256_final(&sha, digest);
    hex_encode(digest, sizeof(digest), output);
    return 1;
}

static int big_cmp(const UINT8 *a, const UINT8 *b, UINTN length) {
    UINTN i;
    for (i = 0; i < length; i++) {
        if (a[i] < b[i]) {
            return -1;
        }
        if (a[i] > b[i]) {
            return 1;
        }
    }
    return 0;
}

static void big_sub_mod(UINT8 *value, const UINT8 *modulus, UINTN length) {
    UINTN i = length;
    UINT32 borrow = 0;

    while (i > 0) {
        UINT32 left;
        UINT32 right;
        i--;
        left = value[i];
        right = ((UINT32)modulus[i]) + borrow;
        if (left < right) {
            value[i] = (UINT8)(left + 256U - right);
            borrow = 1;
        } else {
            value[i] = (UINT8)(left - right);
            borrow = 0;
        }
    }
}

static void big_double_mod(UINT8 *value, const UINT8 *modulus, UINTN length) {
    UINTN i = length;
    UINT32 carry = 0;

    while (i > 0) {
        UINT32 current;
        i--;
        current = (((UINT32)value[i]) << 1) | carry;
        value[i] = (UINT8)current;
        carry = (current >> 8) & 1U;
    }
    if (carry || big_cmp(value, modulus, length) >= 0) {
        big_sub_mod(value, modulus, length);
    }
}

static void big_add_mod(UINT8 *value, const UINT8 *addend, const UINT8 *modulus, UINTN length) {
    UINTN i = length;
    UINT32 carry = 0;

    while (i > 0) {
        UINT32 current;
        i--;
        current = ((UINT32)value[i]) + ((UINT32)addend[i]) + carry;
        value[i] = (UINT8)current;
        carry = (current >> 8) & 1U;
    }
    if (carry || big_cmp(value, modulus, length) >= 0) {
        big_sub_mod(value, modulus, length);
    }
}

static int big_bit_is_set(const UINT8 *value, UINTN bit_index) {
    UINTN byte_index = bit_index / 8U;
    UINTN bit_in_byte = 7U - (bit_index % 8U);
    return (value[byte_index] & (1U << bit_in_byte)) != 0;
}

static void big_mul_mod(UINT8 *output, const UINT8 *a, const UINT8 *b, const UINT8 *modulus, UINTN length) {
    UINT8 result[AVB_MAX_RSA_BYTES];
    UINTN bit;
    UINTN total_bits = length * 8U;

    zero_bytes(result, length);
    for (bit = 0; bit < total_bits; bit++) {
        big_double_mod(result, modulus, length);
        if (big_bit_is_set(b, bit)) {
            big_add_mod(result, a, modulus, length);
        }
    }
    copy_bytes(output, result, length);
}

static void rsa_pow65537(UINT8 *output, const UINT8 *signature, const UINT8 *modulus, UINTN length) {
    UINT8 result[AVB_MAX_RSA_BYTES];
    UINT8 scratch[AVB_MAX_RSA_BYTES];
    UINTN i;

    copy_bytes(result, signature, length);
    for (i = 0; i < 16U; i++) {
        big_mul_mod(scratch, result, result, modulus, length);
        copy_bytes(result, scratch, length);
    }
    big_mul_mod(output, result, signature, modulus, length);
}

static int rsa_verify_sha256_pkcs1_v15(
    const UINT8 *signature,
    const UINT8 *modulus,
    UINTN length,
    const UINT8 digest[SHA256_DIGEST_BYTES]) {
    static const UINT8 sha256_der_prefix[19] = {
        0x30, 0x31, 0x30, 0x0d, 0x06, 0x09, 0x60, 0x86,
        0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01, 0x05,
        0x00, 0x04, 0x20
    };
    UINT8 decoded[AVB_MAX_RSA_BYTES];
    UINTN ff_count;
    UINTN zero_offset;
    UINTN prefix_offset;
    UINTN digest_offset;
    UINTN i;

    if (length == 0 || length > AVB_MAX_RSA_BYTES || big_cmp(signature, modulus, length) >= 0) {
        return 0;
    }

    rsa_pow65537(decoded, signature, modulus, length);

    if (length < SHA256_DIGEST_BYTES + sizeof(sha256_der_prefix) + 11U) {
        return 0;
    }
    ff_count = length - SHA256_DIGEST_BYTES - sizeof(sha256_der_prefix) - 3U;
    zero_offset = 2U + ff_count;
    prefix_offset = zero_offset + 1U;
    digest_offset = prefix_offset + sizeof(sha256_der_prefix);

    if (decoded[0] != 0x00 || decoded[1] != 0x01 || decoded[zero_offset] != 0x00) {
        return 0;
    }
    for (i = 2U; i < zero_offset; i++) {
        if (decoded[i] != 0xff) {
            return 0;
        }
    }
    if (!bytes_equal(decoded + prefix_offset, sha256_der_prefix, sizeof(sha256_der_prefix))) {
        return 0;
    }
    return bytes_equal(decoded + digest_offset, digest, SHA256_DIGEST_BYTES);
}

static int verify_vbmeta_signature(UINT8 *image, UINTN image_size) {
    UINT64 auth_size;
    UINT64 aux_size;
    UINTN verified_size;
    UINT32 algorithm_type;
    UINT64 hash_offset;
    UINT64 hash_size;
    UINT64 signature_offset;
    UINT64 signature_size;
    UINT64 public_key_offset;
    UINT64 public_key_size;
    UINTN key_bytes;
    UINT8 digest[SHA256_DIGEST_BYTES];
    SHA256_CONTEXT sha;
    UINT8 *auth_block;
    UINT8 *aux_block;
    UINT8 *stored_digest;
    UINT8 *signature;
    UINT8 *embedded_key;
    UINT8 *modulus;

    if (HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE == 0U ||
        !vbmeta_image_size(image, image_size, &verified_size)) {
        return 0;
    }

    auth_size = read_be64(image, 12);
    aux_size = read_be64(image, 20);
    algorithm_type = read_be32(image, 28);
    hash_offset = read_be64(image, 32);
    hash_size = read_be64(image, 40);
    signature_offset = read_be64(image, 48);
    signature_size = read_be64(image, 56);
    public_key_offset = read_be64(image, 64);
    public_key_size = read_be64(image, 72);

    if (verified_size != AVB_VBMETA_HEADER_SIZE + (UINTN)auth_size + (UINTN)aux_size) {
        return 0;
    }

    if (algorithm_type == AVB_ALGORITHM_TYPE_SHA256_RSA2048) {
        key_bytes = 256U;
    } else if (algorithm_type == AVB_ALGORITHM_TYPE_SHA256_RSA4096) {
        key_bytes = 512U;
    } else {
        return 0;
    }

    if (hash_size != SHA256_DIGEST_BYTES ||
        signature_size != key_bytes ||
        public_key_size != HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE ||
        public_key_size != 8U + (2U * key_bytes) ||
        read_be32(HomeHarborTrustedAvbPublicKey, 0) != key_bytes * 8U) {
        return 0;
    }

    if (hash_offset > auth_size ||
        hash_size > auth_size - hash_offset ||
        signature_offset > auth_size ||
        signature_size > auth_size - signature_offset ||
        public_key_offset > aux_size ||
        public_key_size > aux_size - public_key_offset) {
        return 0;
    }

    auth_block = image + AVB_VBMETA_HEADER_SIZE;
    aux_block = auth_block + auth_size;
    stored_digest = auth_block + hash_offset;
    signature = auth_block + signature_offset;
    embedded_key = aux_block + public_key_offset;
    modulus = embedded_key + 8U;

    if (!bytes_equal(embedded_key, HomeHarborTrustedAvbPublicKey, public_key_size)) {
        return 0;
    }

    sha256_init(&sha);
    sha256_update(&sha, image, AVB_VBMETA_HEADER_SIZE);
    sha256_update(&sha, aux_block, (UINTN)aux_size);
    sha256_final(&sha, digest);
    if (!bytes_equal(stored_digest, digest, SHA256_DIGEST_BYTES)) {
        return 0;
    }

    return rsa_verify_sha256_pkcs1_v15(signature, modulus, key_bytes, digest);
}

static int vbmeta_preflight_bypass_warning_disabled(void) {
    UINT8 disabled = 0;

    if (!read_uint8_variable(L"HomeHarborVbmetaPreflightWarningDisabled", &HomeHarborBootVariableGuid, &disabled)) {
        return 0;
    }

    return disabled == 1;
}

static EFI_STATUS disable_vbmeta_preflight_warning(void) {
    UINT8 disabled = 1;

    if (!gRT || !gRT->SetVariable) {
        return 1;
    }

    return gRT->SetVariable(
        L"HomeHarborVbmetaPreflightWarningDisabled",
        &HomeHarborBootVariableGuid,
        EFI_VARIABLE_NON_VOLATILE | EFI_VARIABLE_BOOTSERVICE_ACCESS | EFI_VARIABLE_RUNTIME_ACCESS,
        sizeof(disabled),
        &disabled);
}

static CHAR16 read_vbmeta_preflight_choice(void) {
    UINTN ticks;
    CHAR16 key;

    for (ticks = 0; ticks < VBMETA_PREFLIGHT_WARNING_TIMEOUT_SECONDS * 10U; ticks++) {
        if (read_console_key(&key)) {
            if (key == L'1' || key == L'\r') {
                return L'1';
            }
            if (key == L'2') {
                return L'2';
            }
            if (key == L'3') {
                return L'3';
            }
        }
        if (!gBS || !gBS->Stall) {
            return L'1';
        }
        gBS->Stall(100000);
    }

    return L'1';
}

static void halt_boot(CHAR16 *message) {
    print(message);
    print(L"\r\nHomeHarborBoot: boot halted.\r\n");
    while (1) {
        if (gBS && gBS->Stall) {
            gBS->Stall(1000000);
        }
    }
}

static int allow_vbmeta_preflight_bypass(void) {
    CHAR16 choice;

    if (vbmeta_preflight_bypass_warning_disabled()) {
        print(L"HomeHarborBoot: bypassing vbmeta preflight warning by saved preference.\r\n");
        return 1;
    }

    drain_console_input();
    print(L"\r\nWARNING: HomeHarbor vbmeta signature preflight failed.\r\n");
    print(L"Boot integrity cannot be confirmed before Linux starts.\r\n\r\n");
    print(L"1. Continue this time (10s timeout)\r\n");
    print(L"2. Do not show this again\r\n");
    print(L"3. Enter Firmware settings to config\r\n\r\n");
    print(L"Select 1, 2, or 3: ");

    choice = read_vbmeta_preflight_choice();
    print(L"\r\n");

    if (choice == L'2') {
        if (disable_vbmeta_preflight_warning() != EFI_SUCCESS) {
            print(L"HomeHarborBoot: could not save vbmeta warning preference; continuing.\r\n");
        } else {
            print(L"HomeHarborBoot: vbmeta preflight warning disabled; continuing.\r\n");
        }
        return 1;
    }

    if (choice == L'3') {
        if (enter_firmware_settings() != EFI_SUCCESS) {
            print(L"HomeHarborBoot: firmware settings handoff is not supported; continuing.\r\n");
            if (gBS && gBS->Stall) {
                gBS->Stall(3000000);
            }
        }
        return 1;
    }

    print(L"HomeHarborBoot: continuing after vbmeta preflight failure.\r\n");
    return 1;
}
int verify_vbmeta_signature_preflight(
    const CHAR16 *vbmeta_label,
    int secure_boot_active,
    char vbmeta_digest[VBMETA_DIGEST_BUFFER_SIZE]) {
    void *buffer = NULL;
    UINTN size = 0;
    EFI_STATUS status;
    int verified = 0;
    int digest_available = 0;

    vbmeta_digest[0] = 0;

    status = read_raw_partition(vbmeta_label, &buffer, &size);
    if (!EFI_ERROR(status) && buffer && size > 0) {
        digest_available = calculate_vbmeta_digest((UINT8 *)buffer, size, vbmeta_digest);
        verified = verify_vbmeta_signature((UINT8 *)buffer, size);
    }
    if (buffer) {
        gBS->FreePool(buffer);
    }

    if (verified) {
        return digest_available;
    }

    print(L"HomeHarborBoot: vbmeta signature preflight failed\r\n");
    if (secure_boot_active) {
        halt_boot(L"HomeHarborBoot: refusing to boot with Secure Boot enabled and invalid vbmeta.");
    }
    allow_vbmeta_preflight_bypass();
    if (!digest_available) {
        print(L"HomeHarborBoot: refusing to boot without a usable vbmeta digest handoff.\r\n");
        return 0;
    }
    return 1;
}
