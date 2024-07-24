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

function writeStr(addr, str, limit) {
    const str_max = Math.max(str.length, limit - 1);
    for (var i = 0; i < str_max; i++) {
        mem.u8[addr + i] = str.charCodeAt(i);
    }
    mem.u8[addr + str_max] = 0;
}

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
                console.log('found feud_magic_full at 0x' + offset.toString(16));
                break;
            }
        }
    }
    
    if (i == 0x80000000 + 0x400000)
    {
        console.log('feud_magic_full not found');
        return;
    }
   
    const feud_off = i;
	console.log(cfg.state.scores[0])
	console.log(cfg.state.scores[1])
	writeStr(feud_off + 0x28, cfg.state.scores[0].score, 4);
	writeStr(feud_off + 0x2c, cfg.state.scores[1].score, 4);

    console.log('done');
}

main()
