// genicon writes the NasDrive application icon (multi-size PNG based .ico).
//
//	go run ./scripts/genicon gui/NasDrive/Assets/NasDrive.ico
package main

import (
	"bytes"
	"encoding/binary"
	"image"
	"image/color"
	"image/png"
	"log"
	"math"
	"os"
)

func main() {
	if len(os.Args) != 2 {
		log.Fatal("usage: genicon out.ico")
	}
	sizes := []int{16, 24, 32, 48, 64, 128, 256}
	var pngs [][]byte
	for _, s := range sizes {
		var buf bytes.Buffer
		if err := png.Encode(&buf, draw(s)); err != nil {
			log.Fatal(err)
		}
		pngs = append(pngs, buf.Bytes())
	}
	var out bytes.Buffer
	_ = binary.Write(&out, binary.LittleEndian, [3]uint16{0, 1, uint16(len(sizes))})
	offset := 6 + 16*len(sizes)
	for i, s := range sizes {
		b := byte(s)
		if s >= 256 {
			b = 0
		}
		_ = binary.Write(&out, binary.LittleEndian, struct {
			W, H, Colors, Reserved byte
			Planes, BPP            uint16
			Size, Offset           uint32
		}{b, b, 0, 0, 1, 32, uint32(len(pngs[i])), uint32(offset)})
		offset += len(pngs[i])
	}
	for _, p := range pngs {
		out.Write(p)
	}
	if err := os.WriteFile(os.Args[1], out.Bytes(), 0o644); err != nil {
		log.Fatal(err)
	}
}

// draw renders a blue rounded tile with a white drive and a network dot.
func draw(s int) image.Image {
	img := image.NewNRGBA(image.Rect(0, 0, s, s))
	f := float64(s)
	blue := color.NRGBA{0x0B, 0x63, 0xB5, 0xFF}
	white := color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF}
	green := color.NRGBA{0x3F, 0xC3, 0x80, 0xFF}
	// Supersample 4x4 for anti-aliasing.
	const ss = 4
	for y := 0; y < s; y++ {
		for x := 0; x < s; x++ {
			var r, g, b, a float64
			for sy := 0; sy < ss; sy++ {
				for sx := 0; sx < ss; sx++ {
					px := (float64(x) + (float64(sx)+0.5)/ss) / f
					py := (float64(y) + (float64(sy)+0.5)/ss) / f
					c, ok := sample(px, py, blue, white, green)
					if !ok {
						continue
					}
					r += float64(c.R)
					g += float64(c.G)
					b += float64(c.B)
					a += 255
				}
			}
			n := float64(ss * ss)
			if a > 0 {
				img.SetNRGBA(x, y, color.NRGBA{uint8(r * 255 / a), uint8(g * 255 / a), uint8(b * 255 / a), uint8(a / n)})
			}
		}
	}
	return img
}

func roundRect(px, py, x0, y0, x1, y1, rad float64) bool {
	if px < x0 || px > x1 || py < y0 || py > y1 {
		return false
	}
	cx := math.Max(x0+rad, math.Min(px, x1-rad))
	cy := math.Max(y0+rad, math.Min(py, y1-rad))
	return math.Hypot(px-cx, py-cy) <= rad
}

func sample(px, py float64, blue, white, green color.NRGBA) (color.NRGBA, bool) {
	if !roundRect(px, py, 0.02, 0.02, 0.98, 0.98, 0.2) {
		return color.NRGBA{}, false
	}
	// drive body
	if roundRect(px, py, 0.16, 0.30, 0.84, 0.66, 0.08) {
		// slot and LED cut-outs
		if roundRect(px, py, 0.26, 0.45, 0.56, 0.51, 0.03) {
			return blue, true
		}
		if math.Hypot(px-0.71, py-0.48) < 0.05 {
			return green, true
		}
		return white, true
	}
	// network stem and base line
	if px > 0.47 && px < 0.53 && py > 0.66 && py < 0.80 {
		return white, true
	}
	if roundRect(px, py, 0.26, 0.78, 0.74, 0.83, 0.025) {
		return white, true
	}
	return blue, true
}
