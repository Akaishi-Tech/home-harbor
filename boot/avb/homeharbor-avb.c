#include <errno.h>
#include <inttypes.h>
#include <openssl/sha.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define AVB_HEADER_SIZE 256
#define AVB_HASHTREE_TAG 1
#define AVB_HASHTREE_FIXED_SIZE 180

static uint32_t read_be32(const uint8_t *p) {
    return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | (uint32_t)p[3];
}

static uint64_t read_be64(const uint8_t *p) {
    return ((uint64_t)read_be32(p) << 32) | (uint64_t)read_be32(p + 4);
}

static void hex_encode(const uint8_t *data, size_t len, char *out) {
    static const char alphabet[] = "0123456789abcdef";
    for (size_t i = 0; i < len; i++) {
        out[i * 2] = alphabet[data[i] >> 4];
        out[i * 2 + 1] = alphabet[data[i] & 0x0f];
    }
    out[len * 2] = '\0';
}

static int read_file(const char *path, uint8_t **data, size_t *size) {
    FILE *file = fopen(path, "rb");
    long end;
    size_t read_bytes;

    if (!file) {
        fprintf(stderr, "failed to open %s: %s\n", path, strerror(errno));
        return 1;
    }
    if (fseek(file, 0, SEEK_END) != 0 || (end = ftell(file)) < 0 || fseek(file, 0, SEEK_SET) != 0) {
        fprintf(stderr, "failed to stat %s\n", path);
        fclose(file);
        return 1;
    }
    *size = (size_t)end;
    *data = malloc(*size);
    if (!*data) {
        fprintf(stderr, "failed to allocate %zu bytes\n", *size);
        fclose(file);
        return 1;
    }
    read_bytes = fread(*data, 1, *size, file);
    fclose(file);
    if (read_bytes != *size) {
        fprintf(stderr, "failed to read %s\n", path);
        free(*data);
        *data = NULL;
        return 1;
    }
    return 0;
}

static int print_descriptor(const uint8_t *image, size_t image_size, const char *partition, const char *expected_digest) {
    uint64_t auth_size;
    uint64_t aux_size;
    uint64_t descriptors_offset;
    uint64_t descriptors_size;
    uint64_t aux_start;
    uint64_t descriptors_start;
    uint64_t descriptors_end;
    uint64_t offset;
    char vbmeta_digest[SHA256_DIGEST_LENGTH * 2 + 1];
    uint8_t digest[SHA256_DIGEST_LENGTH];
    int found = 0;

    if (image_size < AVB_HEADER_SIZE || memcmp(image, "AVB0", 4) != 0) {
        fprintf(stderr, "input is not an AVB vbmeta image\n");
        return 1;
    }

    auth_size = read_be64(image + 12);
    aux_size = read_be64(image + 20);
    descriptors_offset = read_be64(image + 96);
    descriptors_size = read_be64(image + 104);
    aux_start = AVB_HEADER_SIZE + auth_size;
    descriptors_start = aux_start + descriptors_offset;
    descriptors_end = descriptors_start + descriptors_size;

    if (aux_start > image_size || aux_size > image_size - aux_start ||
        descriptors_start > image_size || descriptors_end > image_size ||
        descriptors_end < descriptors_start) {
        fprintf(stderr, "vbmeta descriptor ranges are invalid\n");
        return 1;
    }

    SHA256(image, AVB_HEADER_SIZE + auth_size + aux_size, digest);
    hex_encode(digest, sizeof(digest), vbmeta_digest);
    if (expected_digest && expected_digest[0] && strcmp(vbmeta_digest, expected_digest) != 0) {
        fprintf(stderr, "vbmeta digest mismatch: expected %s, actual %s\n", expected_digest, vbmeta_digest);
        return 1;
    }

    offset = descriptors_start;
    while (offset + 16 <= descriptors_end) {
        uint64_t tag = read_be64(image + offset);
        uint64_t following = read_be64(image + offset + 8);
        uint64_t descriptor_size = 16 + following;

        if (following > descriptors_end - offset - 16 || descriptor_size < 16) {
            fprintf(stderr, "vbmeta descriptor size is invalid\n");
            return 1;
        }

        if (tag == AVB_HASHTREE_TAG && descriptor_size >= AVB_HASHTREE_FIXED_SIZE) {
            const uint8_t *desc = image + offset;
            uint32_t partition_len = read_be32(desc + 104);
            uint32_t salt_len = read_be32(desc + 108);
            uint32_t root_digest_len = read_be32(desc + 112);
            uint64_t dynamic_size = (uint64_t)partition_len + salt_len + root_digest_len;
            const uint8_t *dynamic = desc + AVB_HASHTREE_FIXED_SIZE;

            if (AVB_HASHTREE_FIXED_SIZE + dynamic_size <= descriptor_size &&
                strlen(partition) == partition_len &&
                memcmp(dynamic, partition, partition_len) == 0) {
                uint64_t image_data_size = read_be64(desc + 20);
                uint64_t tree_offset = read_be64(desc + 28);
                uint64_t tree_size = read_be64(desc + 36);
                uint32_t data_block_size = read_be32(desc + 44);
                uint32_t hash_block_size = read_be32(desc + 48);
                char hash_algorithm[33];
                char *salt_hex = malloc((size_t)salt_len * 2 + 1);
                char *root_digest_hex = malloc((size_t)root_digest_len * 2 + 1);

                if (!salt_hex || !root_digest_hex) {
                    fprintf(stderr, "failed to allocate descriptor output\n");
                    free(salt_hex);
                    free(root_digest_hex);
                    return 1;
                }
                memcpy(hash_algorithm, desc + 72, 32);
                hash_algorithm[32] = '\0';
                for (int i = 31; i >= 0 && hash_algorithm[i] == '\0'; i--) {
                    hash_algorithm[i] = '\0';
                }
                hex_encode(dynamic + partition_len, salt_len, salt_hex);
                hex_encode(dynamic + partition_len + salt_len, root_digest_len, root_digest_hex);

                printf("VBMETA_DIGEST=%s\n", vbmeta_digest);
                printf("PARTITION_NAME=%s\n", partition);
                printf("IMAGE_SIZE=%" PRIu64 "\n", image_data_size);
                printf("TREE_OFFSET=%" PRIu64 "\n", tree_offset);
                printf("TREE_SIZE=%" PRIu64 "\n", tree_size);
                printf("DATA_BLOCK_SIZE=%u\n", data_block_size);
                printf("HASH_BLOCK_SIZE=%u\n", hash_block_size);
                printf("DATA_BLOCKS=%" PRIu64 "\n", image_data_size / data_block_size);
                printf("HASH_ALGORITHM=%s\n", hash_algorithm);
                printf("SALT=%s\n", salt_hex);
                printf("ROOT_DIGEST=%s\n", root_digest_hex);
                free(salt_hex);
                free(root_digest_hex);
                found = 1;
            }
        }

        offset += descriptor_size;
    }

    if (!found) {
        fprintf(stderr, "vbmeta has no hashtree descriptor for %s\n", partition);
        return 1;
    }
    return 0;
}

int main(int argc, char **argv) {
    uint8_t *image = NULL;
    size_t image_size = 0;
    int status;

    if (argc != 4 && argc != 5) {
        fprintf(stderr, "usage: homeharbor-avb descriptor VBMETA_IMAGE PARTITION_NAME [EXPECTED_VBMETA_DIGEST]\n");
        return 2;
    }
    if (strcmp(argv[1], "descriptor") != 0) {
        fprintf(stderr, "unknown command: %s\n", argv[1]);
        return 2;
    }
    if (read_file(argv[2], &image, &image_size) != 0) {
        return 1;
    }
    status = print_descriptor(image, image_size, argv[3], argc == 5 ? argv[4] : NULL);
    free(image);
    return status;
}
