#include "erofs_reader.h"
#include "util.h"

#define EROFS_SUPER_OFFSET 1024ULL
#define EROFS_SUPER_SIZE 144U
#define EROFS_MAGIC 0xE0F5E1E2U
#define EROFS_INODE_SLOT_BYTES 32ULL
#define EROFS_INODE_COMPACT_BYTES 32U
#define EROFS_INODE_EXTENDED_BYTES 64U
#define EROFS_INODE_FLAT_PLAIN 0U
#define EROFS_INODE_FLAT_INLINE 2U
#define EROFS_NULL_ADDR 0xffffffffULL
#define EROFS_FEATURE_INCOMPAT_48BIT 0x00000080U
#define EROFS_DIRENT_BYTES 12U
#define EROFS_DIRENT_NID_MASK 0x7fffffffffffffffULL
#define EROFS_S_IFMT 0170000U
#define EROFS_S_IFREG 0100000U
#define EROFS_S_IFDIR 0040000U

typedef struct {
    UINT32 block_size;
    UINT32 meta_blkaddr;
    UINT64 root_nid;
} EROFS_SUPER;

typedef struct {
    UINT64 nid;
    UINT64 offset;
    UINT64 size;
    UINT64 start_block;
    UINT16 mode;
    UINT16 xattr_icount;
    UINT8 layout;
    UINT8 inode_size;
} EROFS_INODE;

static int add_overflows(UINT64 a, UINT64 b) {
    return a > UINT64_MAX - b;
}

static EFI_STATUS erofs_read(HOMEHARBOR_EROFS_READER *reader, UINT64 offset, UINTN size, void *buffer) {
    if (!reader || !reader->Read || !buffer || add_overflows(offset, size) || offset + size > reader->ImageSize) {
        return 1;
    }
    return reader->Read(reader->Context, offset, size, buffer);
}

static EFI_STATUS erofs_alloc(HOMEHARBOR_EROFS_READER *reader, UINTN size, void **buffer) {
    if (!reader || !reader->Allocate || !buffer || size == 0) {
        return 1;
    }
    *buffer = NULL;
    return reader->Allocate(size, buffer);
}

static void erofs_free(HOMEHARBOR_EROFS_READER *reader, void *buffer) {
    if (reader && reader->Free && buffer) {
        reader->Free(buffer);
    }
}

static EFI_STATUS read_super(HOMEHARBOR_EROFS_READER *reader, EROFS_SUPER *super) {
    UINT8 raw[EROFS_SUPER_SIZE];
    UINT32 feature_incompat;
    UINT8 blkszbits;

    if (erofs_read(reader, EROFS_SUPER_OFFSET, sizeof(raw), raw) != EFI_SUCCESS) {
        return 1;
    }
    if (read_u32(raw, 0) != EROFS_MAGIC) {
        return 1;
    }

    blkszbits = raw[12];
    if (blkszbits < 9 || blkszbits > 16) {
        return 1;
    }
    super->block_size = 1U << blkszbits;
    super->meta_blkaddr = read_u32(raw, 40);
    feature_incompat = read_u32(raw, 80);
    super->root_nid = (feature_incompat & EROFS_FEATURE_INCOMPAT_48BIT) ? read_u64(raw, 112) : (UINT64)read_u16(raw, 14);
    return super->root_nid == 0 ? 1 : EFI_SUCCESS;
}

static EFI_STATUS read_inode(HOMEHARBOR_EROFS_READER *reader, const EROFS_SUPER *super, UINT64 nid, EROFS_INODE *inode) {
    UINT8 raw[EROFS_INODE_EXTENDED_BYTES];
    UINT64 inode_offset = ((UINT64)super->meta_blkaddr * super->block_size) + (nid * EROFS_INODE_SLOT_BYTES);
    UINT16 format;

    if (erofs_read(reader, inode_offset, sizeof(raw), raw) != EFI_SUCCESS) {
        return 1;
    }

    format = read_u16(raw, 0);
    inode->nid = nid;
    inode->offset = inode_offset;
    inode->layout = (UINT8)((format >> 1) & 0x07U);
    inode->xattr_icount = read_u16(raw, 2);
    inode->mode = read_u16(raw, 4);
    if (inode->xattr_icount != 0) {
        return 1;
    }

    if ((format & 0x01U) == 0) {
        inode->inode_size = EROFS_INODE_COMPACT_BYTES;
        inode->size = read_u32(raw, 8);
        inode->start_block = read_u32(raw, 16);
    } else {
        inode->inode_size = EROFS_INODE_EXTENDED_BYTES;
        inode->size = read_u64(raw, 8);
        inode->start_block = read_u32(raw, 16);
        inode->start_block |= ((UINT64)read_u16(raw, 6)) << 32;
    }

    return EFI_SUCCESS;
}

static EFI_STATUS read_inode_data(
    HOMEHARBOR_EROFS_READER *reader,
    const EROFS_SUPER *super,
    const EROFS_INODE *inode,
    int allow_inline,
    void **buffer,
    UINTN *size) {
    UINT64 data_offset;

    *buffer = NULL;
    *size = 0;
    if (inode->size == 0 || inode->size > (UINT64)BOOT_IMAGE_MAX_BYTES) {
        return 1;
    }
    if (inode->size > UINT64_MAX || inode->size > (UINT64)((UINTN)-1)) {
        return 1;
    }

    *size = (UINTN)inode->size;
    if (erofs_alloc(reader, *size, buffer) != EFI_SUCCESS || !*buffer) {
        return 1;
    }

    if (inode->layout == EROFS_INODE_FLAT_PLAIN) {
        if (inode->start_block == EROFS_NULL_ADDR) {
            erofs_free(reader, *buffer);
            *buffer = NULL;
            *size = 0;
            return 1;
        }
        data_offset = inode->start_block * (UINT64)super->block_size;
    } else if (allow_inline && inode->layout == EROFS_INODE_FLAT_INLINE && inode->size <= super->block_size) {
        data_offset = inode->offset + inode->inode_size;
    } else {
        erofs_free(reader, *buffer);
        *buffer = NULL;
        *size = 0;
        return 1;
    }

    if (erofs_read(reader, data_offset, *size, *buffer) != EFI_SUCCESS) {
        erofs_free(reader, *buffer);
        *buffer = NULL;
        *size = 0;
        return 1;
    }

    return EFI_SUCCESS;
}

static int component_matches(const char *component, UINTN component_length, UINT8 *name, UINTN name_length) {
    UINTN i;
    while (name_length > 0 && name[name_length - 1] == 0) {
        name_length--;
    }
    if (component_length != name_length) {
        return 0;
    }
    for (i = 0; i < component_length; i++) {
        if ((UINT8)component[i] != name[i]) {
            return 0;
        }
    }
    return 1;
}

static EFI_STATUS find_child(
    HOMEHARBOR_EROFS_READER *reader,
    const EROFS_SUPER *super,
    const EROFS_INODE *directory,
    const char *component,
    UINTN component_length,
    UINT64 *child_nid) {
    void *data = NULL;
    UINTN size = 0;
    UINTN block_offset;
    UINT8 *bytes;

    if ((directory->mode & EROFS_S_IFMT) != EROFS_S_IFDIR ||
        read_inode_data(reader, super, directory, 1, &data, &size) != EFI_SUCCESS) {
        return 1;
    }

    bytes = (UINT8 *)data;
    for (block_offset = 0; block_offset < size; block_offset += super->block_size) {
        UINTN block_size = size - block_offset;
        UINT8 *block = bytes + block_offset;
        UINT16 first_nameoff;
        UINTN count;
        UINTN i;

        if (block_size > super->block_size) {
            block_size = super->block_size;
        }
        if (block_size < EROFS_DIRENT_BYTES) {
            continue;
        }

        first_nameoff = read_u16(block, 8);
        if (first_nameoff < EROFS_DIRENT_BYTES || first_nameoff > block_size || (first_nameoff % EROFS_DIRENT_BYTES) != 0) {
            erofs_free(reader, data);
            return 1;
        }
        count = first_nameoff / EROFS_DIRENT_BYTES;

        for (i = 0; i < count; i++) {
            UINTN entry = i * EROFS_DIRENT_BYTES;
            UINTN name_start = read_u16(block, entry + 8);
            UINTN name_end = (i + 1 < count) ? read_u16(block, entry + EROFS_DIRENT_BYTES + 8) : block_size;

            if (name_start > name_end || name_end > block_size) {
                erofs_free(reader, data);
                return 1;
            }
            if (component_matches(component, component_length, block + name_start, name_end - name_start)) {
                *child_nid = read_u64(block, entry) & EROFS_DIRENT_NID_MASK;
                erofs_free(reader, data);
                return *child_nid == 0 ? 1 : EFI_SUCCESS;
            }
        }
    }

    erofs_free(reader, data);
    return 1;
}

EFI_STATUS erofs_read_file(HOMEHARBOR_EROFS_READER *reader, const char *path, void **buffer, UINTN *size) {
    EROFS_SUPER super;
    EROFS_INODE current;
    const char *cursor = path;

    if (!buffer || !size || !path) {
        return 1;
    }
    *buffer = NULL;
    *size = 0;
    if (read_super(reader, &super) != EFI_SUCCESS ||
        read_inode(reader, &super, super.root_nid, &current) != EFI_SUCCESS) {
        return 1;
    }

    while (*cursor == '/') {
        cursor++;
    }
    while (*cursor) {
        const char *start = cursor;
        UINTN component_length;
        UINT64 child_nid;

        while (*cursor && *cursor != '/') {
            cursor++;
        }
        component_length = (UINTN)(cursor - start);
        if (component_length == 0) {
            return 1;
        }

        if (*cursor == '/') {
            if (find_child(reader, &super, &current, start, component_length, &child_nid) != EFI_SUCCESS ||
                read_inode(reader, &super, child_nid, &current) != EFI_SUCCESS) {
                return 1;
            }
            while (*cursor == '/') {
                cursor++;
            }
            continue;
        }

        if (find_child(reader, &super, &current, start, component_length, &child_nid) != EFI_SUCCESS ||
            read_inode(reader, &super, child_nid, &current) != EFI_SUCCESS ||
            (current.mode & EROFS_S_IFMT) != EROFS_S_IFREG ||
            current.layout != EROFS_INODE_FLAT_PLAIN) {
            return 1;
        }
        return read_inode_data(reader, &super, &current, 0, buffer, size);
    }

    return 1;
}
