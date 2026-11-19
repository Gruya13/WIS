using System;
using System.Collections.Generic;
using System.Windows;

namespace WIS.HwTest.Core
{
    public class MazeNode
    {
        public int X { get; set; }
        public int Y { get; set; }
        public bool Visited { get; set; }
        public bool WallTop { get; set; }
        public bool WallRight { get; set; }
        public bool WallBottom { get; set; }
        public bool WallLeft { get; set; }
        public bool IsCheckpoint { get; set; }
        public int CheckpointIndex { get; set; }

        public MazeNode(int x, int y)
        {
            X = x;
            Y = y;
            WallTop = true;
            WallRight = true;
            WallBottom = true;
            WallLeft = true;
        }
    }

    public class MazeData
    {
        public int Cols { get; set; }
        public int Rows { get; set; }
        public double CellWidth { get; set; }
        public double CellHeight { get; set; }
        public MazeNode[,] Grid { get; set; }
        public List<Point> SolutionPath { get; set; }
        public List<Point> Checkpoints { get; set; }
        public Point StartPoint { get; set; }
        public Point EndPoint { get; set; }

        public MazeData()
        {
            SolutionPath = new List<Point>();
            Checkpoints = new List<Point>();
        }
    }

    public static class MazeGenerator
    {
        public static MazeData Generate(double screenWidth, double screenHeight, double dpi = 96.0)
        {
            MazeData maze = new MazeData();

            // 12 mm u pikselima: 1 inč = 25.4 mm => dpi * (12 / 25.4)
            double corridorPx = dpi * (12.0 / 25.4);
            if (corridorPx < 35.0) corridorPx = 35.0; // minimalna širina za prst
            if (corridorPx > 80.0) corridorPx = 80.0;

            int cols = (int)Math.Max(5, Math.Floor(screenWidth / corridorPx));
            int rows = (int)Math.Max(4, Math.Floor(screenHeight / corridorPx));

            maze.Cols = cols;
            maze.Rows = rows;
            maze.CellWidth = screenWidth / cols;
            maze.CellHeight = screenHeight / rows;
            maze.Grid = new MazeNode[cols, rows];

            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    maze.Grid[x, y] = new MazeNode(x, y);
                }
            }

            // Definišemo ključne tačke koje moraju biti posećene (4 ugla + ivice)
            List<Point> waypoints = new List<Point>
            {
                new Point(0, 0),                           // Gornji levi ugao
                new Point(cols - 1, 0),                    // Gornji desni ugao
                new Point(cols - 1, rows - 1),            // Donji desni ugao
                new Point(0, rows - 1),                    // Donji levi ugao
                new Point(cols / 2, rows / 2)              // Centar ekrana (Cilj)
            };

            // Kreiramo garantovanu glavnu putanju kroz sve ivice
            Stack<MazeNode> pathStack = new Stack<MazeNode>();
            MazeNode current = maze.Grid[0, 0];
            current.Visited = true;
            pathStack.Push(current);

            Random rnd = new Random();

            for (int w = 1; w < waypoints.Count; w++)
            {
                Point target = waypoints[w];
                int tx = (int)target.X;
                int ty = (int)target.Y;

                while (current.X != tx || current.Y != ty)
                {
                    int dx = Math.Sign(tx - current.X);
                    int dy = Math.Sign(ty - current.Y);

                    // Odabir smera sa malom verovatnoćom skretanja radi lavirint efekta
                    int nextX = current.X;
                    int nextY = current.Y;

                    if (dx != 0 && (dy == 0 || rnd.Next(2) == 0))
                    {
                        nextX += dx;
                    }
                    else if (dy != 0)
                    {
                        nextY += dy;
                    }

                    MazeNode nextNode = maze.Grid[nextX, nextY];
                    RemoveWalls(current, nextNode);
                    nextNode.Visited = true;
                    current = nextNode;
                    pathStack.Push(current);
                }

                // Postavi checkpoint na svakom uglu
                current.IsCheckpoint = true;
                current.CheckpointIndex = w;
                maze.Checkpoints.Add(new Point(
                    current.X * maze.CellWidth + maze.CellWidth / 2.0,
                    current.Y * maze.CellHeight + maze.CellHeight / 2.0
                ));
            }

            // Popuni ostatak lavirinta pomoću DFS-a da se popune slepe ulice
            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    if (!maze.Grid[x, y].Visited)
                    {
                        CarvePassages(maze.Grid, x, y, cols, rows, rnd);
                    }
                }
            }

            maze.StartPoint = new Point(maze.CellWidth / 2.0, maze.CellHeight / 2.0);
            MazeNode goal = maze.Grid[cols / 2, rows / 2];
            maze.EndPoint = new Point(goal.X * maze.CellWidth + maze.CellWidth / 2.0, goal.Y * maze.CellHeight + maze.CellHeight / 2.0);

            return maze;
        }

        private static void CarvePassages(MazeNode[,] grid, int cx, int cy, int cols, int rows, Random rnd)
        {
            grid[cx, cy].Visited = true;
            int[] dirs = new int[] { 0, 1, 2, 3 };
            // Shuffle
            for (int i = 0; i < dirs.Length; i++)
            {
                int r = rnd.Next(i, dirs.Length);
                int tmp = dirs[i]; dirs[i] = dirs[r]; dirs[r] = tmp;
            }

            foreach (int dir in dirs)
            {
                int nx = cx;
                int ny = cy;
                if (dir == 0) ny--; // Gore
                else if (dir == 1) nx++; // Desno
                else if (dir == 2) ny++; // Dole
                else if (dir == 3) nx--; // Levo

                if (nx >= 0 && nx < cols && ny >= 0 && ny < rows && !grid[nx, ny].Visited)
                {
                    RemoveWalls(grid[cx, cy], grid[nx, ny]);
                    CarvePassages(grid, nx, ny, cols, rows, rnd);
                }
            }
        }

        private static void RemoveWalls(MazeNode a, MazeNode b)
        {
            int dx = b.X - a.X;
            int dy = b.Y - a.Y;
            if (dx == 1) { a.WallRight = false; b.WallLeft = false; }
            else if (dx == -1) { a.WallLeft = false; b.WallRight = false; }
            else if (dy == 1) { a.WallBottom = false; b.WallTop = false; }
            else if (dy == -1) { a.WallTop = false; b.WallBottom = false; }
        }

        public static List<Point> GenerateSerpentinePath(double width, double height, double padX, double padY, int cols)
        {
            List<Point> pts = new List<Point>();
            if (cols < 2) cols = 2;
            double usableW = Math.Max(120.0, width - 2.0 * padX);
            double usableH = Math.Max(120.0, height - 2.0 * padY);
            double colStep = usableW / (cols - 1);

            for (int c = 0; c < cols; c++)
            {
                double x = padX + c * colStep;
                bool goingUp = (c % 2 == 0); // Kolona 0 krece od dole-levo ka gore
                const int subSteps = 4;
                for (int s = 0; s <= subSteps; s++)
                {
                    double frac = (double)s / subSteps;
                    double y = goingUp
                        ? (height - padY - frac * usableH)
                        : (padY + frac * usableH);
                    pts.Add(new Point(x, y));
                }
            }
            return pts;
        }

        public static List<Point> GenerateInfinityPath(double width, double height, int samplesPerCurve)
        {
            List<Point> pts = new List<Point>();
            if (samplesPerCurve < 16) samplesPerCurve = 16;

            double cx = width * 0.5;
            double cy = height * 0.5;
            double rx = Math.Max(140.0, width * 0.37);
            double ryScale = Math.Max(100.0, height * 0.36) / (0.35355339 * rx);

            // Od -PI/2 do +3*PI/2: centar -> desna petlja -> centar -> leva petlja -> centar
            double tStart = -Math.PI * 0.5;
            double tSpan = Math.PI * 2.0;

            for (int i = 0; i <= samplesPerCurve; i++)
            {
                double t = tStart + tSpan * ((double)i / samplesPerCurve);
                double sinT = Math.Sin(t);
                double cosT = Math.Cos(t);
                double denom = 1.0 + sinT * sinT;

                double x = cx + (rx * cosT) / denom;
                double y = cy + ((rx * sinT * cosT) / denom) * ryScale;
                pts.Add(new Point(x, y));
            }
            return pts;
        }
    }
}
