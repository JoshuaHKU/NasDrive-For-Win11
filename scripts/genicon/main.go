// genicon draws the NasDrive icons: the application icon and three tray
// state icons, each as a multi-size .ico whose small sizes are snapped to the
// pixel grid so they stay crisp in the notification area.
//
//	go run ./scripts/genicon gui/NasDrive/Assets [preview.png]
//
// Design (on a 32-unit grid): a full-bleed rounded tile with a vertical
// gradient whose colour shows the state, a white two-bay NAS with green
// activity LEDs, and a network link (stem + bar) underneath.
package main

import (
	"bytes"
	"encoding/binary"
	"image"
	"image/color"
	"image/draw"
	"image/png"
	"log"
	"math"
	"os"
	"path/filepath"
)

type palette struct{ top, bottom color.NRGBA }

var (
	blue  = palette{color.NRGBA{0x2B, 0x8A, 0xFF, 0xFF}, color.NRGBA{0x0B, 0x5C, 0xD6, 0xFF}}
	gray  = palette{color.NRGBA{0x9A, 0xA0, 0xA8, 0xFF}, color.NRGBA{0x6B, 0x70, 0x79, 0xFF}}
	amber = palette{color.NRGBA{0xFF, 0xB8, 0x3D, 0xFF}, color.NRGBA{0xDB, 0x88, 0x00, 0xFF}}
	white = color.NRGBA{0xFF, 0xFF, 0xFF, 0xFF}
	led   = color.NRGBA{0x35, 0xD0, 0x7F, 0xFF}
)

func main() {
	if len(os.Args) < 2 {
		log.Fatal("usage: genicon ASSETS_DIR [preview.png]")
	}
	dir := os.Args[1]
	appSizes := []int{16, 20, 24, 32, 40, 48, 64, 128, 256}
	traySizes := []int{16, 20, 24, 32, 40, 48, 64}
	writeIco(filepath.Join(dir, "NasDrive.ico"), blue, appSizes)
	writeIco(filepath.Join(dir, "tray-on.ico"), blue, traySizes)
	writeIco(filepath.Join(dir, "tray-off.ico"), gray, traySizes)
	writeIco(filepath.Join(dir, "tray-warn.ico"), amber, traySizes)
	if len(os.Args) > 2 {
		writePreview(os.Args[2])
	}
}

func writeIco(path string, p palette, sizes []int) {
	var pngs [][]byte
	for _, s := range sizes {
		var buf bytes.Buffer
		if err := png.Encode(&buf, render(s, p)); err != nil {
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
	for _, b := range pngs {
		out.Write(b)
	}
	if err := os.WriteFile(path, out.Bytes(), 0o644); err != nil {
		log.Fatal(err)
	}
}

// shape is a filled region in pixel coordinates.
type shape interface{ contains(x, y float64) bool }

type rrect struct{ x0, y0, x1, y1, r float64 }

func (q rrect) contains(x, y float64) bool {
	if x < q.x0 || x > q.x1 || y < q.y0 || y > q.y1 {
		return false
	}
	cx := math.Max(q.x0+q.r, math.Min(x, q.x1-q.r))
	cy := math.Max(q.y0+q.r, math.Min(y, q.y1-q.r))
	return math.Hypot(x-cx, y-cy) <= q.r
}

type circle struct{ cx, cy, r float64 }

func (c circle) contains(x, y float64) bool { return math.Hypot(x-c.cx, y-c.cy) <= c.r }

type layer struct {
	s shape
	c func(y float64) color.NRGBA
}

// render draws the icon at s×s pixels.
func render(s int, p palette) image.Image {
	f := float64(s) / 32 // design units -> pixels
	small := s <= 24
	// snap rounds an edge to the pixel grid on small icons.
	snap := func(v float64) float64 {
		if small {
			return math.Round(v * f)
		}
		return v * f
	}
	// box builds a rectangle at least min pixels thick in each direction.
	box := func(x0, y0, x1, y1, r float64) rrect {
		a, b, c, d := snap(x0), snap(y0), snap(x1), snap(y1)
		if c-a < 1 {
			c = a + 1
		}
		if d-b < 1 {
			d = b + 1
		}
		rr := r * f
		if small {
			rr = math.Min(rr, 1)
		}
		return rrect{a, b, c, d, rr}
	}
	fs := float64(s)
	tileColor := func(y float64) color.NRGBA { return lerp(p.top, p.bottom, y/fs) }
	solid := func(c color.NRGBA) func(float64) color.NRGBA { return func(float64) color.NRGBA { return c } }

	radius := 7.0
	if s <= 16 {
		radius = 6
	}
	layers := []layer{
		{rrect{0, 0, fs, fs, radius * f}, tileColor},
		// NAS chassis
		{box(6, 4, 26, 21, 2.5), solid(white)},
		// bay separator (tile colour shows through)
		{box(6, 12, 26, 13.5, 0), tileColor},
		// network stem and bar
		{box(14.5, 21, 17.5, 24, 0), solid(white)},
		{box(5, 24, 27, 27.5, 1.5), solid(white)},
	}
	if s >= 32 {
		// drive slots in each bay
		layers = append(layers,
			layer{box(9, 7.25, 17, 8.75, 0.75), tileColor},
			layer{box(9, 16.5, 17, 18, 0.75), tileColor},
			// end nodes on the network bar
			layer{box(3, 23, 7, 28.5, 1.5), solid(white)},
			layer{box(25, 23, 29, 28.5, 1.5), solid(white)},
		)
	}
	// activity LEDs: squares on small icons, dots on larger ones
	if small {
		layers = append(layers,
			layer{box(20.5, 6, 23, 10, 0), solid(led)},
			layer{box(20.5, 16, 23, 20, 0), solid(led)},
		)
	} else {
		layers = append(layers,
			layer{circle{22 * f, 8 * f, 1.6 * f}, solid(led)},
			layer{circle{22 * f, 17.25 * f, 1.6 * f}, solid(led)},
		)
	}

	img := image.NewNRGBA(image.Rect(0, 0, s, s))
	const ss = 8 // 8x8 supersampling
	for py := 0; py < s; py++ {
		for px := 0; px < s; px++ {
			var r, g, b, a float64
			for sy := 0; sy < ss; sy++ {
				for sx := 0; sx < ss; sx++ {
					x := float64(px) + (float64(sx)+0.5)/ss
					y := float64(py) + (float64(sy)+0.5)/ss
					var c color.NRGBA
					hit := false
					for _, l := range layers {
						if l.s.contains(x, y) {
							c, hit = l.c(y), true
						}
					}
					if hit {
						r += float64(c.R)
						g += float64(c.G)
						b += float64(c.B)
						a++
					}
				}
			}
			if a > 0 {
				img.SetNRGBA(px, py, color.NRGBA{uint8(r / a), uint8(g / a), uint8(b / a), uint8(a * 255 / (ss * ss))})
			}
		}
	}
	return img
}

func lerp(a, b color.NRGBA, t float64) color.NRGBA {
	t = math.Max(0, math.Min(1, t))
	m := func(x, y uint8) uint8 { return uint8(float64(x) + (float64(y)-float64(x))*t + 0.5) }
	return color.NRGBA{m(a.R, b.R), m(a.G, b.G), m(a.B, b.B), 255}
}

// writePreview shows every state at tray sizes on light and dark taskbars,
// plus 8x magnified 16/24 px versions to check pixel alignment.
func writePreview(path string) {
	sizes := []int{16, 20, 24, 32, 48}
	pals := []palette{blue, gray, amber}
	w, h := 900, 560
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	draw.Draw(img, image.Rect(0, 0, w, h/2), &image.Uniform{color.NRGBA{0xF3, 0xF3, 0xF3, 0xFF}}, image.Point{}, draw.Src)
	draw.Draw(img, image.Rect(0, h/2, w, h), &image.Uniform{color.NRGBA{0x20, 0x20, 0x20, 0xFF}}, image.Point{}, draw.Src)
	for half := 0; half < 2; half++ {
		y := 20 + half*h/2
		for pi, p := range pals {
			x := 20
			for _, s := range sizes {
				put(img, render(s, p), x, y+pi*60, 1)
				x += s + 16
			}
			x += 20
			for _, s := range []int{16, 24} {
				put(img, render(s, p), x, y+pi*60-10, 3)
				x += s*3 + 20
			}
		}
		// big magnified 16 px for pixel inspection
		put(img, render(16, blue), 560, y, 8)
		put(img, render(24, blue), 700, y, 7)
	}
	f, err := os.Create(path)
	if err != nil {
		log.Fatal(err)
	}
	defer f.Close()
	if err := png.Encode(f, img); err != nil {
		log.Fatal(err)
	}
}

func put(dst *image.NRGBA, src image.Image, x, y, scale int) {
	b := src.Bounds()
	for sy := 0; sy < b.Dy()*scale; sy++ {
		for sx := 0; sx < b.Dx()*scale; sx++ {
			c := color.NRGBAModel.Convert(src.At(sx/scale, sy/scale)).(color.NRGBA)
			if c.A == 0 {
				continue
			}
			bg := dst.NRGBAAt(x+sx, y+sy)
			al := float64(c.A) / 255
			mix := func(f, g uint8) uint8 { return uint8(float64(f)*al + float64(g)*(1-al)) }
			dst.SetNRGBA(x+sx, y+sy, color.NRGBA{mix(c.R, bg.R), mix(c.G, bg.G), mix(c.B, bg.B), 255})
		}
	}
}
