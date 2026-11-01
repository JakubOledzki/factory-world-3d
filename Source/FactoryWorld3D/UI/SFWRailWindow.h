#pragma once

#include "CoreMinimal.h"
#include "Dom/JsonObject.h"
#include "Widgets/SCompoundWidget.h"

class AFWPlayerController;
class FFWForm;
class SBox;
class SScrollBox;
struct FFWNetReply;

/**
 * The railway station of the plot (T), as in the 2D game (station_window.gd), in five tabs:
 *   Trasy: freight routes between the player's own plots, from loading chests to receiving chests, and naming the plot
 *     (freight.get / add / remove, world.rename)
 *   Magazyn: the goods waiting in the station, unloaded to the inventory and loaded from it (station.unload / load)
 *   Wyślij: one train (or a ship from a port, a truck, a sled or a horse cart) to another plot (station.send)
 *   Rozkład: routes whose trains leave by themselves every few minutes (station.route / unroute)
 *   W drodze: trains coming and gone, with countdowns
 * A train goes to another plot of the player's or to another player's station, found by their name (station.find).
 */
class SFWRailWindow : public SCompoundWidget
{
public:
	SLATE_BEGIN_ARGS(SFWRailWindow) {}
		SLATE_ARGUMENT(TWeakObjectPtr<AFWPlayerController>, Controller)
	SLATE_END_ARGS()

	void Construct(const FArguments& InArgs);

	virtual bool SupportsKeyboardFocus() const override { return true; }
	virtual FReply OnKeyDown(const FGeometry& Geometry, const FKeyEvent& Event) override;
	virtual void Tick(const FGeometry& Geometry, const double CurrentTime, const float DeltaTime) override;

	// Also used by the test console commands
	void ShowTab(int32 Tab);
	void StockAction(bool bLoad, const FString& Item, int32 Quantity);

private:
	void Load(bool bPoll = false);
	void LoadFreight(bool bPoll = false);
	void Act(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, const FText& Toast);
	void FreightAct(const TCHAR* Type, TSharedPtr<FJsonObject> Payload, const FText& Toast);
	void Rebuild();
	TSharedRef<SWidget> MakeFreight();
	TSharedRef<SWidget> MakeStock();
	TSharedRef<SWidget> MakeSend();
	TSharedRef<SWidget> MakeRoutes();
	TSharedRef<SWidget> MakeTrips();
	TSharedRef<SWidget> MakeDestination();
	TSharedRef<SWidget> MakeTransportChoice(bool bRoute);
	FText TripInfo(int32 Quantity, bool bRoute) const;
	FIntPoint Destination() const;
	FString Where() const;
	FString PlotLabel(FIntPoint At) const;
	TSharedPtr<FJsonObject> FreightPlot(FIntPoint At) const;
	double Km(FIntPoint From, FIntPoint To) const;
	double ServerNow() const;
	bool Typing() const;

	TWeakObjectPtr<AFWPlayerController> Controller;
	FIntPoint Plot = FIntPoint(-1, -1);
	int32 Tab = 0;
	TSharedPtr<FJsonObject> State;
	TSharedPtr<FJsonObject> Freight;
	TArray<FIntPoint> MyPlots;
	TArray<TSharedPtr<FJsonObject>> Found;
	TMap<FString, FString> ItemNames;
	// What stands on the plot and where it lies: a port, a truck depot, a stable; on the glacier or the tundra a sled
	bool bPort = false;
	bool bTruck = false;
	bool bStable = false;
	bool bPolar = false;
	int32 WorldWidth = 6933;
	double ClockOffset = 0.0;
	double NextPoll = 0.0;
	double NextFreightPoll = 0.0;
	// The route being set up: items (none: everything), the freight load, the items picked for a timetable route
	TArray<TPair<FString, int32>> Cargo;
	TArray<FString> RouteItems;
	TSharedPtr<FFWForm> Form;
	TSharedPtr<SBox> Content;
	TSharedPtr<SScrollBox> Scroll;
};
