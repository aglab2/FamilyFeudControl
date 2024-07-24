const file = _native.fsOpen("%LocalAppData%/aglab2/FamilyFeud/1.0.0.0/feud_config.json", "r")
var buffer = new ArrayBuffer(0x10000)
const read_amt = _native.fsRead(file, buffer, 0, 0x10000, 0)
_native.fsClose(file)

function arrayBufferToString(buffer, length)
{
    var bufView = new Uint8Array(buffer);
    var result = '';
    var addition = Math.pow(2,16)-1;

    for(var i = 0; i < length; i += addition)
	{
        if (i + addition > length)
		{
            addition = length - i;
        }
        result += String.fromCharCode.apply(null, bufView.subarray(i, i + addition));
    }

    return result;
}

const data = arrayBufferToString(buffer, read_amt);
const cfg = JSON.parse(data)

function main() {
    const feud_magic = 0x54484520;
    const feud_magic_full = "THE FAMILY FEUD CONTROL START YE"
    
    for (var i = 0x80000000; i < 0x80000000 + 0x400000; i += 0x100) {
        var probe = mem.u32[i];
        if (probe == feud_magic)
        {
            var offset = i;
            var probe_full = mem.getblock(i, feud_magic_full.length);
            if (probe_full == feud_magic_full)
            {
                break;
            }
        }
    }
    
    if (i == 0x80000000 + 0x400000)
    {
        console.log('feud_magic_full not found');
        return;
    }
    
    console.clear();
    const feud_off = i;
    for (var i = 0; i < 11; i++) {
        const cmd = mem.u32[feud_off + 0x34 + i * 4];
        if (cmd == 0x00000000)
            break;
    
        const cmd_str = mem.getstring(cmd);
        console.log(cmd_str)
    }
}

main()
