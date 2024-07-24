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

function writeAnswers(answers, off) {
    for (var j = 0; j < answers.length; j++) {
        writeStr(off + 0x20 * j + 0x00, answers[j].name, 28);
        writeStr(off + 0x20 * j + 0x1c, answers[j].cost, 4);
    }
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

    const teams = cfg.teams;
    for (var i = 0; i < teams.length; i++) {
        const team_off = feud_off + 0x60 + 0xB0 * i;
        writeStr(team_off, teams[i].teamName, 16);
        for (var j = 0; j < 5; j++) {
            writeStr(team_off + 0x10 + 0x20 * j, teams[i].players[j].name, 32);
        }
    }

    const round = cfg.rounds;
    for (var i = 0; i < 5; i++) {
        writeAnswers(round[i].answers, feud_off + 0x1c0 + 0x100 * i);
    }

    const final = cfg.final;
    writeAnswers(final.answersInit, feud_off + 0x6c0);
    writeAnswers(final.answersAfter, feud_off + 0x760);

    console.log('done');
}

main()
