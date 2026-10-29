# Dispatch activity catalog — one to five suspects

Behavior branches are individual choices: `Fleeing_Event` → `flee`, `Resisting_Event` → `resist`, `Compliant_Event` → `comply`. The event reader supplies each occurrence’s authored weights to Dispatch. A group does not share a single roll.

Activity starts on acceptance, as clarified in this chat. Actors and assets are prepared beforehand; activity confirmation comes from later native observations.

## One suspect

| Event ID | Incident | Setting | Activity | Civilian roles |
|---|---|---|---|---|
| `reckless_driver` | Reckless Driver Reported | road_vehicle | `driving_fast` | 0 victim(s); 0+ witnesses/staff/customers |
| `vehicle_theft` | Vehicle Theft In Progress | any | `parked_theft` | 0 victim(s); 0+ witnesses/staff/customers |
| `carjacking` | Carjacking Reported | any | `carjacking` | 1 victim(s); 0+ witnesses/staff/customers |
| `vehicle_pursuit` | Vehicle Pursuit Requested | road_vehicle | `driving_fast` | 0 victim(s); 0+ witnesses/staff/customers |
| `disturbance` | Disturbance Reported | any | `disturbance` | 1 victim(s); 0+ witnesses/staff/customers |
| `store_robbery` | Store Robbery Reported | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `store_robbery_interior_single` | Store Robbery — Armed Suspect Inside | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery` | Bank Robbery Reported | financial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `pedestrian_pursuit` | Suspect Fleeing On Foot | any | `existing lifecycle` | 0 victim(s); 0+ witnesses/staff/customers |
| `armed_business_disturbance` | Armed Disturbance Near a Business | commercial | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `road_vehicle_speed_chaos` | Road Vehicle Speed Chaos | road_vehicle | `driving_fast` | 0 victim(s); 0+ witnesses/staff/customers |
| `road_foot_shot_fired` | Road Foot Suspect — Shots Fired | road_foot | `shooting` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_shot_fired_single` | Shots Fired — One Suspect in an Alley | alley | `shooting` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_robbery` | Clothing Store Robbery | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_robbery` | Liquor Store Robbery | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `convenience_store_robbery` | Convenience Store Robbery | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `bank_robbery_escape` | Bank Robbery — Suspects Attempt to Escape | financial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `alley_attack_attempt` | Attempted Attack in an Alley | alley | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `roadside_attack_attempt` | Attempted Attack Near a Road | road_foot | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `roadside_vehicle_kidnapping` | Vehicle Kidnapping in Progress | road_vehicle | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `roadside_vehicle_kidnapping_attempt` | Attempted Vehicle Kidnapping Near a Road | road_vehicle | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `roadside_carjacking_attempt` | Attempted Carjacking Near a Road | road_vehicle | `carjacking` | 1 victim(s); 0+ witnesses/staff/customers |
| `vehicle_shootout_pursuit` | Armed Vehicle Pursuit — Shots Fired | road_vehicle | `driving_fast` | 0 victim(s); 0+ witnesses/staff/customers |
| `alley_murder_victim_scene` | Alley Murder — Suspect Near Victim | alley | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `store_robbery_mapped_interior_attempt` | Armed Robbery Inside a Cataloged Store | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_attempt` | Clothing Store Robbery — Suspect Inside | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_mapped_interior_attempt` | Gun Store Robbery — One Suspect Holds the Male Clerk | Interior GunStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_clerk_armed_assault_mapped_interior` | Armed Assault on Gun Store Clerk | Interior GunStore | `threat` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_customer_armed_assault_mapped_interior` | Armed Assault on Customer — Gun Clerk Present | Interior GunStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_clerk_snatch_mapped_interior` | Armed Snatch Attempt — Male Gun Clerk | Interior GunStore | `victim_snatch` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_customer_snatch_mapped_interior` | Armed Customer Snatch — Male Gun Clerk Present | Interior GunStore | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_clerk_murder_mapped_interior` | Gun Store Murder — Male Clerk Dead, Suspect Nearby | Interior GunStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_customer_murder_mapped_interior` | Gun Store Murder — Customer Dead, Male Clerk Present | Interior GunStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_attempt` | Bank Robbery — Banker and Customers Threatened | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `interior_attack_attempt` | Attempted Attack Inside an Accessible Building | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `barber_shop_clerk_assault_mapped_interior` | Clerk Assault Inside a Barber Shop | Interior BarberShop | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `barber_shop_clerk_snatch_mapped_interior` | Clerk Snatch Attempt Inside a Barber Shop | Interior BarberShop | `victim_snatch` | 1 victim(s); 0+ witnesses/staff/customers |
| `interior_murder_victim_scene` | Murder Scene Inside an Accessible Building | Interior  | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `residential_burglary_occupied_home_attempt` | Occupied Home Break-In — Resident Threatened | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_attempted_murder` | Attempted Murder in an Alley | alley | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `roadside_attempted_murder` | Attempted Murder Near a Road | road_foot | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_kidnapping_attempt` | Kidnapping Attempt in an Alley | alley | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `convenience_store_mapped_interior_robbery` | Convenience Store Robbery — Clerk and Customers Threatened | Interior ConvenienceStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `convenience_store_clerk_assault_mapped_interior` | Clerk Assault Inside a Convenience Store | Interior ConvenienceStore | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `convenience_store_customer_assault_mapped_interior` | Customer Assault Inside a Convenience Store | Interior ConvenienceStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `convenience_store_clerk_snatch_mapped_interior` | Clerk Snatch Attempt Inside a Convenience Store | Interior ConvenienceStore | `victim_snatch` | 1 victim(s); 0+ witnesses/staff/customers |
| `convenience_store_customer_snatch_mapped_interior` | Customer Snatch Attempt Inside a Convenience Store | Interior ConvenienceStore | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `convenience_store_murder_mapped_interior` | Murder Scene Inside a Convenience Store | Interior ConvenienceStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_mapped_interior_robbery` | Liquor Store Robbery — Clerk and Customers Threatened | Interior LiquorStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `liquor_store_clerk_assault_mapped_interior` | Clerk Assault Inside a Liquor Store | Interior LiquorStore | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_customer_assault_mapped_interior` | Customer Assault Inside a Liquor Store | Interior LiquorStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `liquor_store_clerk_snatch_mapped_interior` | Clerk Snatch Attempt Inside a Liquor Store | Interior LiquorStore | `victim_snatch` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_customer_snatch_mapped_interior` | Customer Snatch Attempt Inside a Liquor Store | Interior LiquorStore | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `liquor_store_murder_mapped_interior` | Murder Scene Inside a Liquor Store | Interior LiquorStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_mapped_interior_jacking` | Tattoo Shop Armed Jacking - Clerk and Customer Threatened | Interior TattooShop | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_armed_assault_mapped_interior` | Armed Assault on Tattoo Clerk | Interior TattooShop | `threat` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_armed_assault_mapped_interior` | Armed Assault on Tattoo Customer - Clerk Present | Interior TattooShop | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_assault_mapped_interior` | Assault on Tattoo Clerk | Interior TattooShop | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_assault_mapped_interior` | Assault on Tattoo Customer - Clerk Present | Interior TattooShop | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_snatch_mapped_interior` | Tattoo Clerk Snatch Attempt | Interior TattooShop | `victim_snatch` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_snatch_mapped_interior` | Tattoo Shop Customer Snatch - Clerk Present | Interior TattooShop | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_murder_mapped_interior` | Tattoo Shop Murder - Clerk Dead, Suspect Nearby | Interior TattooShop | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_murder_mapped_interior` | Tattoo Shop Murder - Customer Dead, Clerk Present | Interior TattooShop | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_single_robber` | Bank Robbery — One Armed Suspect Threatens the Banker | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_gunfire_single` | Bank Robbery — Shots Reported With One Suspect Inside | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_customers_threatened` | Bank Heist — Customers Held at Gunpoint With the Banker | Interior Bank | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `clothing_store_mapped_interior_armed_attack` | Armed Assault on a Clothing Store Clerk | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_assault` | Unarmed Assault on a Clothing Store Clerk | Interior ClothingStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_assault` | Customer Assault Inside a Clothing Store — Clerk Present | Interior ClothingStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_snatch` | Clerk Snatch Attempt Inside a Clothing Store | Interior ClothingStore | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_snatch` | Customer Snatch Attempt Inside a Clothing Store — Clerk Present | Interior ClothingStore | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_murder` | Murder Inside a Clothing Store — Clerk Dead, Suspect Nearby | Interior ClothingStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_murder` | Murder Inside a Clothing Store — Customer Dead, Clerk Present | Interior ClothingStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_murder_victim_scene` | Outdoor Murder Scene - Suspect Near Victim | any | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_attempted_murder` | Attempted Murder at an Outdoor Location | any | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_kidnapping_attempt` | Outdoor Kidnapping Attempt - Victim Present | any | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `disturbance_stalking` | Stalking and unwanted following | any | `stalking` | 1 victim(s); 1+ witnesses/staff/customers |
| `disturbance_lying_naked` | Lying naked disturbance | any | `lying` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_physical_assault` | Physical assault on a pedestrian | any | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_knife_assault` | Knife assault on a pedestrian | any | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_gun_assault` | Gun assault on a pedestrian | any | `shooting` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_people_theft` | Theft from a pedestrian | any | `theft` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_driver_assault` | Assault on a driver beside their vehicle | any | `driver_assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `road_vehicle_ramming` | Vehicle ramming and roadside chaos | road_vehicle | `ramming` | 1 victim(s); 1+ witnesses/staff/customers |
| `road_vehicle_slow_disturbance` | Slow-driving road disturbance | road_vehicle | `driving_slow` | 1 victim(s); 1+ witnesses/staff/customers |
| `interior_theft` | Theft inside a store | Interior Store | `theft` | 1 victim(s); 2+ witnesses/staff/customers |

## Two to three suspects

| Event ID | Incident | Setting | Activity | Civilian roles |
|---|---|---|---|---|
| `carjacking` | Carjacking Reported | any | `carjacking` | 1 victim(s); 0+ witnesses/staff/customers |
| `armed_suspect` | Armed Suspect Reported | any | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `shots_fired` | Shots Fired Reported | any | `shooting` | 1 victim(s); 1+ witnesses/staff/customers |
| `store_robbery` | Store Robbery Reported | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `bank_robbery` | Bank Robbery Reported | financial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `kidnapping` | Kidnapping In Progress | any | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `violent_disturbance` | Violent Disturbance Reported | any | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `road_gang_ambush` | Road Gang Ambush Reported | road_vehicle | `disturbance` | 0 victim(s); 0+ witnesses/staff/customers |
| `alley_drug_deal` | Possible Drug Deal in an Alley | alley | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_criminal_meeting` | Criminal Meeting Reported in an Alley | alley | `gathering` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_money_armory_deal` | Possible Money or Weapons Deal in an Alley | alley | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_robbery` | Clothing Store Robbery | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_robbery` | Gun Store Group Jacking — Clerk and Customers Threatened Inside | Interior GunStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `liquor_store_robbery` | Liquor Store Robbery | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `convenience_store_robbery` | Convenience Store Robbery | commercial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `bank_robbery_escape` | Bank Robbery — Suspects Attempt to Escape | financial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `bank_robbery_gunfire_group` | Bank Robbery — Gunfire Reported | financial | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `alley_group_shootout` | Alley Shootout — Group Suspects | alley | `shooting` | 1 victim(s); 1+ witnesses/staff/customers |
| `roadside_group_shootout` | Roadside Shootout — Group Suspects | road_foot | `shooting` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_attack_attempt` | Attempted Attack in an Alley | alley | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `roadside_attack_attempt` | Attempted Attack Near a Road | road_foot | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `roadside_vehicle_kidnapping` | Vehicle Kidnapping in Progress | road_vehicle | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `roadside_vehicle_kidnapping_attempt` | Attempted Vehicle Kidnapping Near a Road | road_vehicle | `driving_fast` | 1 victim(s); 0+ witnesses/staff/customers |
| `roadside_carjacking_attempt` | Attempted Carjacking Near a Road | road_vehicle | `carjacking` | 1 victim(s); 0+ witnesses/staff/customers |
| `group_two_vehicle_pursuit` | Two-Vehicle Group Pursuit | road_vehicle | `driving_fast` | 0 victim(s); 0+ witnesses/staff/customers |
| `alley_murder_victim_scene` | Alley Murder — Suspect Near Victim | alley | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `store_robbery_mapped_interior_attempt` | Armed Robbery Inside a Cataloged Store | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_clerk_armed_assault_mapped_interior` | Armed Assault on Gun Store Clerk | Interior GunStore | `threat` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_customer_armed_assault_mapped_interior` | Armed Assault on Customer — Gun Clerk Present | Interior GunStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_clerk_murder_mapped_interior` | Gun Store Murder — Male Clerk Dead, Suspect Nearby | Interior GunStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_customer_murder_mapped_interior` | Gun Store Murder — Customer Dead, Male Clerk Present | Interior GunStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_attempt` | Bank Robbery — Banker and Customers Threatened | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `interior_attack_attempt` | Attempted Attack Inside an Accessible Building | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `barber_shop_clerk_assault_mapped_interior` | Clerk Assault Inside a Barber Shop | Interior BarberShop | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `interior_murder_victim_scene` | Murder Scene Inside an Accessible Building | Interior  | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `residential_burglary_occupied_home_attempt` | Occupied Home Break-In — Resident Threatened | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_attempted_murder` | Attempted Murder in an Alley | alley | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `roadside_attempted_murder` | Attempted Murder Near a Road | road_foot | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_kidnapping_attempt` | Kidnapping Attempt in an Alley | alley | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `convenience_store_mapped_interior_robbery` | Convenience Store Robbery — Clerk and Customers Threatened | Interior ConvenienceStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `convenience_store_mapped_interior_jacking` | Convenience Store Jacking — Armed Group and Customers Inside | Interior ConvenienceStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `convenience_store_clerk_assault_mapped_interior` | Clerk Assault Inside a Convenience Store | Interior ConvenienceStore | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `convenience_store_customer_assault_mapped_interior` | Customer Assault Inside a Convenience Store | Interior ConvenienceStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `convenience_store_murder_mapped_interior` | Murder Scene Inside a Convenience Store | Interior ConvenienceStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_mapped_interior_robbery` | Liquor Store Robbery — Clerk and Customers Threatened | Interior LiquorStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `liquor_store_mapped_interior_jacking` | Liquor Store Jacking — Armed Group and Customers Inside | Interior LiquorStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `liquor_store_clerk_assault_mapped_interior` | Clerk Assault Inside a Liquor Store | Interior LiquorStore | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_customer_assault_mapped_interior` | Customer Assault Inside a Liquor Store | Interior LiquorStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `liquor_store_murder_mapped_interior` | Murder Scene Inside a Liquor Store | Interior LiquorStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_mapped_interior_jacking` | Tattoo Shop Armed Jacking - Clerk and Customer Threatened | Interior TattooShop | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_mapped_interior_gunfire` | Tattoo Shop Robbery - Gunfire and Two Suspects Inside | Interior TattooShop | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_armed_assault_mapped_interior` | Armed Assault on Tattoo Clerk | Interior TattooShop | `threat` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_armed_assault_mapped_interior` | Armed Assault on Tattoo Customer - Clerk Present | Interior TattooShop | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_assault_mapped_interior` | Assault on Tattoo Clerk | Interior TattooShop | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_assault_mapped_interior` | Assault on Tattoo Customer - Clerk Present | Interior TattooShop | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_snatch_mapped_interior` | Tattoo Clerk Snatch Attempt | Interior TattooShop | `victim_snatch` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_snatch_mapped_interior` | Tattoo Shop Customer Snatch - Clerk Present | Interior TattooShop | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_murder_mapped_interior` | Tattoo Shop Murder - Clerk Dead, Suspect Nearby | Interior TattooShop | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_murder_mapped_interior` | Tattoo Shop Murder - Customer Dead, Clerk Present | Interior TattooShop | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_group_heist` | Bank Heist — Armed Group Holds the Banker | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_gunfire_group` | Bank Heist — Group Gunfire Inside the Branch | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_three_suspect_takeover` | Bank Heist — Three Suspects Threaten Staff and Customers | Interior Bank | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_two_suspect_gunfire` | Bank Robbery — Shots Reported During a Two-Suspect Heist | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_escape_attempt` | Bank Robbery — Suspects May Flee Through the Entrance | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_customers_threatened` | Bank Heist — Customers Held at Gunpoint With the Banker | Interior Bank | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_group_resistance` | Bank Heist — Group Resists Police Entry | Interior Bank | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `clothing_store_mapped_interior_group_robbery` | Clothing Store Jacking — Clerk and Customers Threatened | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_group_gunfire` | Clothing Store Robbery — Group Gunfire Inside | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_armed_attack` | Armed Assault on a Clothing Store Clerk | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_assault` | Unarmed Assault on a Clothing Store Clerk | Interior ClothingStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_assault` | Customer Assault Inside a Clothing Store — Clerk Present | Interior ClothingStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_snatch` | Clerk Snatch Attempt Inside a Clothing Store | Interior ClothingStore | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_snatch` | Customer Snatch Attempt Inside a Clothing Store — Clerk Present | Interior ClothingStore | `victim_snatch` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_murder` | Murder Inside a Clothing Store — Clerk Dead, Suspect Nearby | Interior ClothingStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_murder` | Murder Inside a Clothing Store — Customer Dead, Clerk Present | Interior ClothingStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_drug_exchange` | Possible Drug Exchange Outdoors | any | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_criminal_meeting` | Criminal Meeting at an Outdoor Location | any | `gathering` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_weapons_exchange` | Possible Weapons Exchange Outdoors | any | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_murder_victim_scene` | Outdoor Murder Scene - Suspect Near Victim | any | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_attempted_murder` | Attempted Murder at an Outdoor Location | any | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_kidnapping_attempt` | Outdoor Kidnapping Attempt - Victim Present | any | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_group_shootout` | Outdoor Group Shootout - Civilian at Risk | any | `shooting` | 1 victim(s); 1+ witnesses/staff/customers |
| `road_vehicle_ramming` | Vehicle ramming and roadside chaos | road_vehicle | `ramming` | 1 victim(s); 1+ witnesses/staff/customers |
| `road_vehicle_slow_disturbance` | Slow-driving road disturbance | road_vehicle | `driving_slow` | 1 victim(s); 1+ witnesses/staff/customers |

## Four to five suspects

| Event ID | Incident | Setting | Activity | Civilian roles |
|---|---|---|---|---|
| `alley_drug_deal` | Possible Drug Deal in an Alley | alley | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_criminal_meeting` | Criminal Meeting Reported in an Alley | alley | `gathering` | 1 victim(s); 1+ witnesses/staff/customers |
| `alley_money_armory_deal` | Possible Money or Weapons Deal in an Alley | alley | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_robbery` | Gun Store Group Jacking — Clerk and Customers Threatened Inside | Interior GunStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `alley_murder_victim_scene` | Alley Murder — Suspect Near Victim | alley | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `store_robbery_mapped_interior_attempt` | Armed Robbery Inside a Cataloged Store | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_clerk_armed_assault_mapped_interior` | Armed Assault on Gun Store Clerk | Interior GunStore | `threat` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_customer_armed_assault_mapped_interior` | Armed Assault on Customer — Gun Clerk Present | Interior GunStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `gun_store_clerk_murder_mapped_interior` | Gun Store Murder — Male Clerk Dead, Suspect Nearby | Interior GunStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `gun_store_customer_murder_mapped_interior` | Gun Store Murder — Customer Dead, Male Clerk Present | Interior GunStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_attempt` | Bank Robbery — Banker and Customers Threatened | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `interior_attack_attempt` | Attempted Attack Inside an Accessible Building | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `barber_shop_clerk_assault_mapped_interior` | Clerk Assault Inside a Barber Shop | Interior BarberShop | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `interior_murder_victim_scene` | Murder Scene Inside an Accessible Building | Interior  | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `residential_burglary_occupied_home_attempt` | Occupied Home Break-In — Resident Threatened | Interior  | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `convenience_store_mapped_interior_robbery` | Convenience Store Robbery — Clerk and Customers Threatened | Interior ConvenienceStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `convenience_store_mapped_interior_jacking` | Convenience Store Jacking — Armed Group and Customers Inside | Interior ConvenienceStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `convenience_store_clerk_assault_mapped_interior` | Clerk Assault Inside a Convenience Store | Interior ConvenienceStore | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `convenience_store_customer_assault_mapped_interior` | Customer Assault Inside a Convenience Store | Interior ConvenienceStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `convenience_store_murder_mapped_interior` | Murder Scene Inside a Convenience Store | Interior ConvenienceStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_mapped_interior_robbery` | Liquor Store Robbery — Clerk and Customers Threatened | Interior LiquorStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `liquor_store_mapped_interior_jacking` | Liquor Store Jacking — Armed Group and Customers Inside | Interior LiquorStore | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `liquor_store_clerk_assault_mapped_interior` | Clerk Assault Inside a Liquor Store | Interior LiquorStore | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `liquor_store_customer_assault_mapped_interior` | Customer Assault Inside a Liquor Store | Interior LiquorStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `liquor_store_murder_mapped_interior` | Murder Scene Inside a Liquor Store | Interior LiquorStore | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_mapped_interior_jacking` | Tattoo Shop Armed Jacking - Clerk and Customer Threatened | Interior TattooShop | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_armed_assault_mapped_interior` | Armed Assault on Tattoo Clerk | Interior TattooShop | `threat` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_armed_assault_mapped_interior` | Armed Assault on Tattoo Customer - Clerk Present | Interior TattooShop | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_assault_mapped_interior` | Assault on Tattoo Clerk | Interior TattooShop | `assault` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_assault_mapped_interior` | Assault on Tattoo Customer - Clerk Present | Interior TattooShop | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `tattoo_store_clerk_murder_mapped_interior` | Tattoo Shop Murder - Clerk Dead, Suspect Nearby | Interior TattooShop | `murder` | 1 victim(s); 0+ witnesses/staff/customers |
| `tattoo_store_customer_murder_mapped_interior` | Tattoo Shop Murder - Customer Dead, Clerk Present | Interior TattooShop | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_group_heist` | Bank Heist — Armed Group Holds the Banker | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_gunfire_group` | Bank Heist — Group Gunfire Inside the Branch | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_escape_attempt` | Bank Robbery — Suspects May Flee Through the Entrance | Interior Bank | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_customers_threatened` | Bank Heist — Customers Held at Gunpoint With the Banker | Interior Bank | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `bank_robbery_mapped_interior_group_resistance` | Bank Heist — Group Resists Police Entry | Interior Bank | `threat` | 1 victim(s); 2+ witnesses/staff/customers |
| `clothing_store_mapped_interior_group_robbery` | Clothing Store Jacking — Clerk and Customers Threatened | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_group_gunfire` | Clothing Store Robbery — Group Gunfire Inside | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_armed_attack` | Armed Assault on a Clothing Store Clerk | Interior ClothingStore | `threat` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_assault` | Unarmed Assault on a Clothing Store Clerk | Interior ClothingStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_assault` | Customer Assault Inside a Clothing Store — Clerk Present | Interior ClothingStore | `assault` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_clerk_murder` | Murder Inside a Clothing Store — Clerk Dead, Suspect Nearby | Interior ClothingStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `clothing_store_mapped_interior_customer_murder` | Murder Inside a Clothing Store — Customer Dead, Clerk Present | Interior ClothingStore | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_drug_exchange` | Possible Drug Exchange Outdoors | any | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_criminal_meeting` | Criminal Meeting at an Outdoor Location | any | `gathering` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_weapons_exchange` | Possible Weapons Exchange Outdoors | any | `exchange` | 1 victim(s); 1+ witnesses/staff/customers |
| `exterior_murder_victim_scene` | Outdoor Murder Scene - Suspect Near Victim | any | `murder` | 1 victim(s); 1+ witnesses/staff/customers |
| `interior_group_dealing` | Dealing inside a store | Interior Store | `exchange` | 1 victim(s); 2+ witnesses/staff/customers |
| `interior_group_gathering` | Group gathering inside a store | Interior Store | `gathering` | 1 victim(s); 2+ witnesses/staff/customers |
| `interior_massacre_chaos` | Massacre aftermath inside a store | Interior ClothingStore | `murder` | 3 victim(s); 1+ witnesses/staff/customers |
| `exterior_massacre_chaos` | Massacre aftermath outdoors | any | `murder` | 3 victim(s); 1+ witnesses/staff/customers |
| `exterior_populated_chaos` | Group assault in a populated place | any | `assault` | 1 victim(s); 4+ witnesses/staff/customers |
| `exterior_store_assault` | Group assault outside a store | commercial | `assault` | 1 victim(s); 2+ witnesses/staff/customers |
| `exterior_threat_gathering` | Threatening group gathering | any | `threat` | 1 victim(s); 2+ witnesses/staff/customers |

