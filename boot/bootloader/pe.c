#include "pe.h"
#include "util.h"

UINTN detect_pe_file_size(UINT8 *buffer, UINTN partition_size) {
    UINTN pe_offset;
    UINTN optional_offset;
    UINTN optional_size;
    UINTN section_offset;
    UINTN end;
    UINT16 sections;
    UINT16 magic;
    UINTN data_directory_offset;
    UINTN security_directory_offset;
    UINTN i;

    if (partition_size < 4096 || buffer[0] != 'M' || buffer[1] != 'Z') {
        return 0;
    }
    if (0x40 > partition_size) {
        return 0;
    }
    pe_offset = read_u32(buffer, 0x3c);
    if (pe_offset + 24 > partition_size) {
        return 0;
    }
    if (buffer[pe_offset] != 'P' || buffer[pe_offset + 1] != 'E' || buffer[pe_offset + 2] != 0 || buffer[pe_offset + 3] != 0) {
        return 0;
    }

    sections = read_u16(buffer, pe_offset + 6);
    optional_size = read_u16(buffer, pe_offset + 20);
    optional_offset = pe_offset + 24;
    section_offset = optional_offset + optional_size;
    if (sections == 0 || optional_size < 96 || section_offset + ((UINTN)sections * 40) > partition_size) {
        return 0;
    }

    magic = read_u16(buffer, optional_offset);
    if (magic == 0x20b) {
        data_directory_offset = optional_offset + 112;
    } else if (magic == 0x10b) {
        data_directory_offset = optional_offset + 96;
    } else {
        return 0;
    }

    end = read_u32(buffer, optional_offset + 60);
    for (i = 0; i < sections; i++) {
        UINTN section = section_offset + (i * 40);
        UINTN raw_size = read_u32(buffer, section + 16);
        UINTN raw_offset = read_u32(buffer, section + 20);
        if (raw_size > 0) {
            if (raw_offset > partition_size || raw_size > partition_size - raw_offset) {
                return 0;
            }
            end = max_un(end, raw_offset + raw_size);
        }
    }

    security_directory_offset = data_directory_offset + (4 * 8);
    if (security_directory_offset + 8 <= optional_offset + optional_size) {
        UINTN cert_offset = read_u32(buffer, security_directory_offset);
        UINTN cert_size = read_u32(buffer, security_directory_offset + 4);
        if (cert_offset != 0 || cert_size != 0) {
            if (cert_offset > partition_size || cert_size > partition_size - cert_offset) {
                return 0;
            }
            end = max_un(end, cert_offset + cert_size);
        }
    }

    return end <= partition_size ? end : 0;
}
